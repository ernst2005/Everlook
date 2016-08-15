﻿//
//  CameraMovement.cs
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
using OpenTK;

namespace Everlook.Viewport.Camera
{
	/// <summary>
	/// Camera movement component. This class is bound to a single camera instance, and handles
	/// relative movement inside the world for it. A number of simple movement methods are exposed
	/// which makes handling it from the outside easier.
	/// </summary>
	public class CameraMovement
	{
		private readonly ViewportCamera Camera;

		/// <summary>
		/// The current orientation of the bound camera.
		/// </summary>
		public Vector3 Orientation
		{
			get
			{
				return new Vector3(MathHelper.RadiansToDegrees(Camera.VerticalViewAngle),
					MathHelper.RadiansToDegrees(Camera.HorizontalViewAngle), 0);
			}
			set
			{
				Camera.VerticalViewAngle = MathHelper.DegreesToRadians(value.X);
				Camera.HorizontalViewAngle = MathHelper.DegreesToRadians(value.Y);
			}
		}

		/// <summary>
		/// The current position of the bound camera.
		/// </summary>
		public Vector3 Position
		{
			get
			{
				return Camera.Position;
			}
			set
			{
				Camera.Position = value;
			}
		}

		/// <summary>
		/// Whether or not to constrain the vertical view angle to -/+ 90 degrees. This
		/// prevents the viewer from going upside down.
		/// </summary>
		public bool ConstrainVerticalView
		{
			get;
			set;
		}

		/// <summary>
		/// The default movement speed of the observer within the viewport.
		/// </summary>
		private const float DefaultMovementSpeed = 10.0f;

		/// <summary>
		/// The default turning speed of the observer within the viewport.
		/// </summary>
		private const float DefaultTurningSpeed = 10.0f;

		/// <summary>
		/// Creates a new <see cref="CameraMovement"/> instance, bound to the input camera.
		/// </summary>
		public CameraMovement(ViewportCamera inCamera)
		{
			this.Camera = inCamera;
			this.ConstrainVerticalView = true;
		}

		/// <summary>
		/// Calculates the relative position of the observer in world space, using
		/// input relayed from the main interface.
		/// </summary>
		public void CalculateMovement(float deltaMouseX, float deltaMouseY, float deltaTime, float forwardAxis,
			float rightAxis, float upAxis)
		{
			// Perform radial movement
			RotateHorizontal(deltaMouseX * DefaultTurningSpeed * deltaTime);
			RotateVertical(deltaMouseY * DefaultTurningSpeed * deltaTime);

			// Constrain the viewing angles to no more than 90 degrees in any direction
			if (ConstrainVerticalView)
			{
				if (Camera.VerticalViewAngle > MathHelper.DegreesToRadians(90.0f))
				{
					Camera.VerticalViewAngle = MathHelper.DegreesToRadians(90.0f);
				}
				else if (Camera.VerticalViewAngle < MathHelper.DegreesToRadians(-90.0f))
				{
					Camera.VerticalViewAngle = MathHelper.DegreesToRadians(-90.0f);
				}
			}

			// Perform axial movement
			if (forwardAxis > 0)
			{
				MoveForward(deltaTime * DefaultMovementSpeed * Math.Abs(forwardAxis));
			}

			if (forwardAxis < 0)
			{
				MoveBackward(deltaTime * DefaultMovementSpeed * Math.Abs(forwardAxis));
			}

			if (rightAxis > 0)
			{
				MoveRight(deltaTime * DefaultMovementSpeed * Math.Abs(rightAxis));
			}

			if (rightAxis < 0)
			{
				MoveLeft(deltaTime * DefaultMovementSpeed * Math.Abs(rightAxis));
			}

			if (upAxis > 0)
			{
				MoveUp(deltaTime * DefaultMovementSpeed * Math.Abs(upAxis));
			}

			if (upAxis < 0)
			{
				MoveDown(deltaTime * DefaultMovementSpeed * Math.Abs(upAxis));
			}
		}

		/// <summary>
		/// Rotates the camera on the horizontal axis by the provided amount of radians.
		/// </summary>
		public void RotateHorizontal(float radians)
		{
			Camera.HorizontalViewAngle += radians;
		}

		/// <summary>
		/// Rotates the camera on the vertical axis by the provided amount of radians.
		/// </summary>
		public void RotateVertical(float radians)
		{
			Camera.VerticalViewAngle += radians;
		}

		/// <summary>
		/// Moves the camera up along its local Y axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveUp(float distance)
		{
			Camera.Position += Camera.UpVector * Math.Abs(distance);
		}

		/// <summary>
		/// Moves the camera down along its local Y axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveDown(float distance)
		{
			Camera.Position -= Camera.UpVector * Math.Abs(distance);
		}

		/// <summary>
		/// Moves the camera forward along its local Z axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveForward(float distance)
		{
			Camera.Position += Camera.LookDirectionVector * Math.Abs(distance);
		}

		/// <summary>
		/// Moves the camera backwards along its local Z axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveBackward(float distance)
		{
			Camera.Position -= Camera.LookDirectionVector * Math.Abs(distance);
		}

		/// <summary>
		/// Moves the camera left along its local X axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveLeft(float distance)
		{
			Camera.Position -= Camera.RightVector * Math.Abs(distance);
		}

		/// <summary>
		/// Moves the camera right along its local X axis by <paramref name="distance"/> units.
		/// </summary>
		public void MoveRight(float distance)
		{
			Camera.Position += Camera.RightVector * Math.Abs(distance);
		}
	}
}