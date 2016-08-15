﻿//
//  ExplorerBuilder.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2016 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Threading;
using System.Collections.Generic;
using Gtk;
using Everlook.Configuration;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Everlook.Package;
using System.Globalization;

namespace Everlook.Explorer
{
	/// <summary>
	/// The Explorer Builder class acts as a background worker for the file explorer, enumerating file nodes as requested.
	/// </summary>
	public sealed class ExplorerBuilder : IDisposable
	{
		/// <summary>
		/// Occurs when a package group has been added.
		/// </summary>
		public event ItemEnumeratedEventHandler PackageGroupAdded;

		/// <summary>
		/// Occurs when a top-level package has been enumerated. This event does not mean that all files in the
		/// package have been enumerated, only that the package has been registered by the builder.
		/// </summary>
		public event ItemEnumeratedEventHandler PackageEnumerated;

		/// <summary>
		/// Occurs when a work order has been completed.
		/// </summary>
		public event ReferenceEnumeratedEventHandler EnumerationFinished;


		private readonly object EnumeratedReferenceQueueLock = new object();
		/// <summary>
		/// A list of enumerated references. This list acts as an intermediate location where the UI can fetch results
		/// when it's idle.
		/// </summary>
		public readonly List<ItemReference> EnumeratedReferences = new List<ItemReference>();

		private ItemEnumeratedEventArgs PackageGroupAddedArgs;
		private ItemEnumeratedEventArgs PackageEnumeratedArgs;
		private ItemEnumeratedEventArgs EnumerationFinishedArgs;

		/// <summary>
		/// The cached package directories. Used when the user adds or removes game directories during runtime.
		/// </summary>
		private List<string> CachedPackageDirectories = new List<string>();

		/// <summary>
		/// The package groups. This is, at a glance, groupings of packages in a game directory
		/// that act as a cohesive unit. Usually, a single package group represents a single game
		/// instance.
		/// </summary>
		private readonly Dictionary<string, PackageGroup> PackageGroups = new Dictionary<string, PackageGroup>();

		/// <summary>
		/// The package node group mapping. Maps package groups to their base virtual item references.
		/// </summary>
		public readonly Dictionary<PackageGroup, VirtualItemReference> PackageGroupVirtualNodeMapping =
			new Dictionary<PackageGroup, VirtualItemReference>();

		/// <summary>
		/// Maps package names and paths to tree nodes.
		/// Key: Path of package, folder or file.
		/// Value: TreeIter that maps to the package, folder or file.
		/// </summary>
		public readonly Dictionary<ItemReference, TreeIter> PackageItemNodeMapping =
			new Dictionary<ItemReference, TreeIter>();

		/// <summary>
		/// Maps tree nodes to package names and paths.
		/// Key: TreeIter that represents the item reference.
		/// Value: ItemReference that the iter maps to.
		/// </summary>
		public readonly Dictionary<TreeIter, ItemReference> PackageNodeItemMapping =
			new Dictionary<TreeIter, ItemReference>();

		/// <summary>
		/// The virtual reference mapping. Maps item references to their virtual counterparts.
		/// Key: PackageGroup that hosts this virtual reference.
		/// Dictionary::Key: An item path in an arbitrary package.
		/// Dictionary::Value: A virtual item reference that hosts the hard item references.
		/// </summary>
		private readonly Dictionary<PackageGroup, Dictionary<string, VirtualItemReference>> VirtualReferenceMappings =
			new Dictionary<PackageGroup, Dictionary<string, VirtualItemReference>>();


		/// <summary>
		/// A queue of work submitted by the UI (and indirectly, the user). Worker threads are given
		/// one reference from this queue to be enumerated, and then it is removed.
		/// </summary>
		private readonly List<ItemReference> WorkQueue = new List<ItemReference>();

		/// <summary>
		/// A queue of references that have not yet been fully enumerated, yet have been submitted to the
		/// work queue. These wait here until they are enumerated, at which point they are resubmitted to the work queue.
		/// </summary>
		private readonly List<ItemReference> WaitQueue = new List<ItemReference>();

		/// <summary>
		/// The main enumeration loop thread. Accepts work from the work queue and distributes it
		/// to the available threads.
		/// </summary>
		private readonly Thread EnumerationLoopThread;

		/// <summary>
		/// The main resubmission loop thread. Takes waiting references and adds them back to the work queue.
		/// </summary>
		private readonly Thread ResubmissionLoopThread;

		/// <summary>
		/// A collection of threads that are currently enumerating a directory.
		/// </summary>
		private readonly List<Thread> ActiveEnumerationThreads = new List<Thread>();

		/// <summary>
		/// The maximum number of enumeration threads that can be active at any one point. This value
		/// is calcuated in the constructor, and is equal to the number of available processor cores
		/// multiplied by 250.
		/// </summary>
		private static readonly int MaxEnumerationThreadCount = Environment.ProcessorCount * 250;

		/// <summary>
		/// Whether or not the explorer builder should currently process any work. Acts as an on/off switch
		/// for the main background thread.
		/// </summary>
		private bool ShouldProcessWork;

		/// <summary>
		/// Whether or not all possible package groups for the provided paths in <see cref="CachedPackageDirectories"/>
		/// have been created and loaded.
		/// </summary>
		private bool ArePackageGroupsLoaded;

		/// <summary>
		/// Whether or not the explorer builder is currently reloading. Reloading constitutes clearing all
		/// enumerated data, and recreating all package groups using the new paths.
		/// </summary>
		private bool IsReloading;


		/// <summary>
		/// Initializes a new instance of the <see cref="Everlook.Explorer.ExplorerBuilder"/> class.
		/// </summary>
		public ExplorerBuilder()
		{
			this.EnumerationLoopThread = new Thread(EnumerationLoop);
			this.ResubmissionLoopThread = new Thread(ResubmissionLoop);
			Reload();
		}

		/// <summary>
		/// Gets a value indicating whether this instance is actively accepting work orders.
		/// </summary>
		/// <value><c>true</c> if this instance is active; otherwise, <c>false</c>.</value>
		public bool IsActive
		{
			get { return ShouldProcessWork; }
		}

		/// <summary>
		/// Starts the enumeration thread in the background.
		/// </summary>
		public void Start()
		{
			if (!EnumerationLoopThread.IsAlive)
			{
				this.ShouldProcessWork = true;

				this.ResubmissionLoopThread.Start();
				this.EnumerationLoopThread.Start();
			}
			else
			{
				throw new ThreadStateException("The enumeration thread has already been started.");
			}
		}

		/// <summary>
		/// Stops the enumeration thread, allowing it to finish the current work order.
		/// </summary>
		public void Stop()
		{
			if (EnumerationLoopThread.IsAlive)
			{
				this.ShouldProcessWork = false;
			}
			else
			{
				throw new ThreadStateException("The enumeration thread has not been started.");
			}
		}

		/// <summary>
		/// Reloads the explorer builder, resetting all list files and known content.
		/// </summary>
		public void Reload()
		{
			if (!IsReloading)
			{
				IsReloading = true;
				ArePackageGroupsLoaded = false;
				Thread t = new Thread(Reload_Implementation);

				t.Start();
			}
		}

		/// <summary>
		/// Loads all packages in the currently selected game directory. This function does not enumerate files
		/// and directories deeper than one to keep the UI responsive.
		/// </summary>
		private void Reload_Implementation()
		{
			if (HasPackageDirectoryChanged())
			{
				CachedPackageDirectories = GamePathStorage.Instance.GamePaths;
				this.PackageGroups.Clear();

				if (CachedPackageDirectories.Count > 0)
				{
					WorkQueue.Clear();
					PackageItemNodeMapping.Clear();
					PackageNodeItemMapping.Clear();

					PackageGroupVirtualNodeMapping.Clear();
					VirtualReferenceMappings.Clear();
				}

				foreach (string packageDirectory in CachedPackageDirectories)
				{
					if (Directory.Exists(packageDirectory))
					{
						// Create the package group and add it to the available ones
						string folderName = Path.GetFileName(packageDirectory);
						PackageGroup group = new PackageGroup(folderName, packageDirectory);

						this.PackageGroups.Add(folderName, group);

						// Create a virtual item reference that points to the package group
						VirtualItemReference packageGroupReference = new VirtualItemReference(group, new ItemReference(group))
						{
							State = ReferenceState.Enumerated
						};

						// Create a virtual package folder for the individual packages under the package group
						ItemReference packageGroupPackagesFolderReference = new ItemReference(group, packageGroupReference, "");

						// Add the package folder as a child to the package group node
						packageGroupReference.ChildReferences.Add(packageGroupPackagesFolderReference);

						// Send the package group node to the UI
						this.PackageGroupAddedArgs = new ItemEnumeratedEventArgs(packageGroupReference);
						RaisePackageGroupAdded();

						// Add the packages in the package group as nodes to the package folder
						foreach (KeyValuePair<string, List<string>> packageListfile in group.PackageListfiles)
						{
							if (packageListfile.Value != null)
							{
								string packageName = Path.GetFileName(packageListfile.Key);
								ItemReference packageReference = new ItemReference(group, packageGroupPackagesFolderReference,
									packageName, "");

								// Send the package node to the UI
								this.PackageEnumeratedArgs = new ItemEnumeratedEventArgs(packageReference);
								RaisePackageEnumerated();

								// Submit the package as a work order, enumerating the topmost directories
								SubmitWork(packageReference);
							}
						}
					}
				}

				IsReloading = false;
				ArePackageGroupsLoaded = true;
			}
		}

		/// <summary>
		/// Determines whether the package directory changed.
		/// </summary>
		/// <returns><c>true</c> if the package directory has changed; otherwise, <c>false</c>.</returns>
		public bool HasPackageDirectoryChanged()
		{
			return !CachedPackageDirectories.OrderBy(t => t).SequenceEqual(GamePathStorage.Instance.GamePaths.OrderBy(t => t));
		}

		/// <summary>
		/// Main loop of this worker class. Enumerates any work placed into the work queue.
		/// </summary>
		private void EnumerationLoop()
		{
			while (ShouldProcessWork)
			{
				if (ActiveEnumerationThreads.Count > 0)
				{
					// Clear out finished threads
					List<Thread> finishedThreads = new List<Thread>();
					foreach (Thread t in ActiveEnumerationThreads)
					{
						if (!t.IsAlive)
						{
							finishedThreads.Add(t);
						}
					}

					foreach (Thread t in finishedThreads)
					{
						ActiveEnumerationThreads.Remove(t);
					}
				}

				if (ArePackageGroupsLoaded && WorkQueue.Count > 0)
				{
					// If there's room for more threads, get the first work order and start a new one
					if (ActiveEnumerationThreads.Count < MaxEnumerationThreadCount)
					{
						// Grab the first item in the queue.
						ItemReference parentReference = WorkQueue.First();
						Thread t = new Thread(() => EnumerateFilesAndFolders(parentReference));
						this.ActiveEnumerationThreads.Add(t);

						WorkQueue.Remove(parentReference);
						t.Start();
					}
				}
			}
		}

		/// <summary>
		/// Submits work to the explorer builder. The work submitted is processed in a
		/// first-in, first-out order as work orders may depend on each other.
		/// </summary>
		/// <param name="reference">Reference.</param>
		public void SubmitWork(ItemReference reference)
		{
			if (!WorkQueue.Contains(reference) && reference.State == ReferenceState.NotEnumerated)
			{
				reference.State = ReferenceState.Enumerating;
				WorkQueue.Add(reference);
			}
			else if (reference.State == ReferenceState.Enumerating)
			{
				WaitQueue.Add(reference);
			}
		}

		/// <summary>
		/// Enumerates the files and subfolders in the specified package, starting at
		/// the provided root path.
		/// </summary>
		/// <param name="parentReference">Parent reference where the search should start.</param>
		private void EnumerateFilesAndFolders(ItemReference parentReference)
		{
			if (parentReference != null)
			{
				VirtualItemReference virtualParentReference = parentReference as VirtualItemReference;
				if (virtualParentReference != null)
				{
					EnumerateHardReference(virtualParentReference.HardReference);

					for (int i = 0; i < virtualParentReference.OverriddenHardReferences.Count; ++i)
					{
						EnumerateHardReference(virtualParentReference.OverriddenHardReferences[i]);
					}

					virtualParentReference.State = ReferenceState.Enumerated;
				}
				else
				{
					EnumerateHardReference(parentReference);
				}
			}
		}

		/// <summary>
		/// Enumerates a hard reference.
		/// </summary>
		/// <param name="hardReference">Hard reference.</param>
		private void EnumerateHardReference(ItemReference hardReference)
		{
			List<ItemReference> localEnumeratedReferences = new List<ItemReference>();
			List<string> packageListFile;
			if (hardReference.PackageGroup.PackageListfiles.TryGetValue(hardReference.PackageName, out packageListFile))
			{
				IEnumerable<string> strippedListfile =
					packageListFile.Where(s => s.StartsWith(hardReference.ItemPath, true, new CultureInfo("en-GB")));
				foreach (string filePath in strippedListfile)
				{
					string childPath = Regex.Replace(filePath, "^(?-i)" + Regex.Escape(hardReference.ItemPath), "");

					int slashIndex = childPath.IndexOf('\\');
					string topDirectory = childPath.Substring(0, slashIndex + 1);

					if (!String.IsNullOrEmpty(topDirectory))
					{
						ItemReference directoryReference = new ItemReference(hardReference.PackageGroup, hardReference, topDirectory);
						if (!hardReference.ChildReferences.Contains(directoryReference))
						{
							hardReference.ChildReferences.Add(directoryReference);

							localEnumeratedReferences.Add(directoryReference);
						}
					}
					else if (String.IsNullOrEmpty(topDirectory) && slashIndex == -1)
					{
						ItemReference fileReference = new ItemReference(hardReference.PackageGroup, hardReference, childPath);
						if (!hardReference.ChildReferences.Contains(fileReference))
						{
							// Files can't have any children, so it will always be enumerated.
							hardReference.State = ReferenceState.Enumerated;
							hardReference.ChildReferences.Add(fileReference);

							localEnumeratedReferences.Add(fileReference);
						}
					}
					else
					{
						break;
					}
				}


				lock (EnumeratedReferenceQueueLock)
				{
					// Add this directory's enumerated files in order as one block
					this.EnumeratedReferences.AddRange(localEnumeratedReferences);
				}

				hardReference.State = ReferenceState.Enumerated;

				EnumerationFinishedArgs = new ItemEnumeratedEventArgs(hardReference);
				RaiseEnumerationFinished();
			}
			else
			{
				throw new InvalidDataException("No listfile was found for the package referenced by this item reference.");
			}
		}

		/// <summary>
		/// The resubmission loop handles waiting references whose parents are currently enumerating. When the parents
		/// are finished, they are readded to the work queue.
		/// </summary>
		private void ResubmissionLoop()
		{
			while (ShouldProcessWork)
			{
				List<ItemReference> readyReferences = new List<ItemReference>();
				for (int i = 0; i < this.WaitQueue.Count; ++i)
				{
					if (this.WaitQueue[i].ParentReference.State == ReferenceState.Enumerated)
					{
						readyReferences.Add(this.WaitQueue[i]);
					}
				}

				foreach (ItemReference readyReference in readyReferences)
				{
					this.WaitQueue.Remove(readyReference);
					SubmitWork(readyReference);
				}
			}
		}

		/// <summary>
		/// Adds a virtual mapping.
		/// </summary>
		/// <param name="hardReference">Hard reference.</param>
		/// <param name="virtualReference">Virtual reference.</param>
		public void AddVirtualMapping(ItemReference hardReference, VirtualItemReference virtualReference)
		{
			PackageGroup referenceGroup = hardReference.PackageGroup;
			if (VirtualReferenceMappings.ContainsKey(referenceGroup))
			{
				if (!VirtualReferenceMappings[referenceGroup].ContainsKey(hardReference.ItemPath))
				{
					VirtualReferenceMappings[referenceGroup].Add(hardReference.ItemPath, virtualReference);
				}
			}
			else
			{
				Dictionary<string, VirtualItemReference> groupDictionary = new Dictionary<string, VirtualItemReference>();
				groupDictionary.Add(hardReference.ItemPath, virtualReference);

				VirtualReferenceMappings.Add(referenceGroup, groupDictionary);
			}
		}

		/// <summary>
		/// Gets a virtual reference.
		/// </summary>
		/// <returns>The virtual reference.</returns>
		/// <param name="hardReference">Hard reference.</param>
		public VirtualItemReference GetVirtualReference(ItemReference hardReference)
		{
			PackageGroup referenceGroup = hardReference.PackageGroup;
			if (VirtualReferenceMappings.ContainsKey(referenceGroup))
			{
				VirtualItemReference virtualReference;
				if (VirtualReferenceMappings[referenceGroup].TryGetValue(hardReference.ItemPath, out virtualReference))
				{
					return virtualReference;
				}
			}

			return null;
		}

		/// <summary>
		/// Raises the package group added event.
		/// </summary>
		private void RaisePackageGroupAdded()
		{
			if (PackageGroupAdded != null)
			{
				PackageGroupAdded(this, PackageGroupAddedArgs);
			}
		}

		/// <summary>
		/// Raises the package enumerated event.
		/// </summary>
		private void RaisePackageEnumerated()
		{
			if (PackageEnumerated != null)
			{
				PackageEnumerated(this, PackageEnumeratedArgs);
			}
		}

		/// <summary>
		/// Raises the enumeration finished event.
		/// </summary>
		private void RaiseEnumerationFinished()
		{
			if (EnumerationFinished != null)
			{
				EnumerationFinished(this, EnumerationFinishedArgs);
			}
		}

		/// <summary>
		/// Releases all resource used by the <see cref="Everlook.Explorer.ExplorerBuilder"/> object.
		/// </summary>
		/// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="Everlook.Explorer.ExplorerBuilder"/>. The
		/// <see cref="Dispose"/> method leaves the <see cref="Everlook.Explorer.ExplorerBuilder"/> in an unusable state.
		/// After calling <see cref="Dispose"/>, you must release all references to the
		/// <see cref="Everlook.Explorer.ExplorerBuilder"/> so the garbage collector can reclaim the memory that the
		/// <see cref="Everlook.Explorer.ExplorerBuilder"/> was occupying.</remarks>
		public void Dispose()
		{
			ShouldProcessWork = false;

			foreach (Thread t in ActiveEnumerationThreads)
			{
				t.Abort();
			}

			foreach (KeyValuePair<string, PackageGroup> packageGroup in this.PackageGroups)
			{
				packageGroup.Value.Dispose();
			}
		}
	}

	/// <summary>
	/// Package enumerated event handler.
	/// </summary>
	public delegate void ItemEnumeratedEventHandler(object sender, ItemEnumeratedEventArgs e);

	/// <summary>
	/// Reference enumerated event handler. Bundles arguments for an event where a reference has been
	/// enumerated.
	/// </summary>
	public delegate void ReferenceEnumeratedEventHandler(object sender, ItemEnumeratedEventArgs e);

	/// <summary>
	/// Item enumerated event arguments.
	/// </summary>
	public class ItemEnumeratedEventArgs : EventArgs
	{
		/// <summary>
		/// Contains the enumerated item reference.
		/// </summary>
		/// <value>The item.</value>
		public ItemReference Item { get; private set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="Everlook.Explorer.ItemEnumeratedEventArgs"/> class.
		/// </summary>
		/// <param name="inItem">In item.</param>
		public ItemEnumeratedEventArgs(ItemReference inItem)
		{
			this.Item = inItem;
		}
	}
}