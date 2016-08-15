﻿//
//  RenderCache.cs
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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Everlook.Utility;
using Everlook.Viewport.Rendering.Core;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using Warcraft.BLP;
using Warcraft.Core;
using GLPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;
using SysPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Everlook.Viewport.Rendering
{
	/// <summary>
	/// OpenGL caching handler for objects that can be used more than once during a run of the program and
	/// may take some time to generate.
	///
	/// Currently, these are textures and shader programs.
	/// </summary>
	public class RenderCache : IDisposable
	{
		private readonly Dictionary<string, int> GLTextureCache = new Dictionary<string, int>();
		private readonly Dictionary<EverlookShader, int> GLShaderCache = new Dictionary<EverlookShader, int>();

		/// <summary>
		/// A singleton instance of the rendering cache.
		/// </summary>
		public static readonly RenderCache Instance = new RenderCache();

		private RenderCache()
		{

		}

		/// <summary>
		/// Determines whether or not the rendering cache has a cached texture id
		/// for the specified texture file path.
		/// </summary>
		public bool HasCachedTextureForPath(string texturePath)
		{
			if (string.IsNullOrEmpty(texturePath))
			{
				throw new ArgumentNullException(nameof(texturePath));
			}

			return this.GLTextureCache.ContainsKey(texturePath.ConvertPathSeparatorsToCurrentNativeSeparator().ToUpperInvariant());
		}

		/// <summary>
		/// Determines whether or not the rendering cache has a cached shader
		/// for the specified shader type.
		/// </summary>
		public bool HasCachedShader(EverlookShader shader)
		{
			if (!Enum.IsDefined(typeof(EverlookShader), shader))
			{
				throw new ArgumentException("An unknown shader was passed to the rendering cache.", nameof(shader));
			}

			return this.GLShaderCache.ContainsKey(shader);
		}

		/// <summary>
		/// Gets a cached texture ID from the rendering cache.
		/// </summary>
		public int GetCachedTexture(string texturePath)
		{
			return this.GLTextureCache[texturePath.ConvertPathSeparatorsToCurrentNativeSeparator().ToUpperInvariant()];
		}

		/// <summary>
		/// Gets a cached shader ID from the rendering cache.
		/// </summary>
		public int GetCachedShader(EverlookShader shader)
		{
			return this.GLShaderCache[shader];
		}

		/// <summary>
		/// Creates a cached texture for the specifed texture, using the specified path
		/// as a lookup key.
		/// </summary>
		public int CreateCachedTexture(BLP texture, string texturePath, TextureWrapMode textureWrapMode = TextureWrapMode.Repeat)
		{
			if (texture == null)
			{
				throw new ArgumentNullException(nameof(texture));
			}

			int textureID = GL.GenTexture();
			if (texture.GetCompressionType() == TextureCompressionType.DXTC)
			{
				try
				{
					LoadDXTTexture(textureID, texture);
				}
				catch (GraphicsErrorException gex)
				{
					Console.WriteLine($"GraphicsErrorException in CreateCachedTexture (failed to create DXT texture): {gex.Message}");
				}
				finally
				{
					// Load a fallback bitmap instead
					using (Bitmap mipZero = texture.GetMipMap(0))
					{
						LoadBitmapTexture(textureID, mipZero);
					}
				}
			}
			else
			{
				using (Bitmap mipZero = texture.GetMipMap(0))
				{
					LoadBitmapTexture(textureID, mipZero);
				}
			}

			// Use linear mipmapped filtering
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);

			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)textureWrapMode);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)textureWrapMode);

			int maximalMipLevel = texture.GetMipMapCount() == 0 ? 0 : texture.GetMipMapCount() - 1;
			GL.TexParameterI(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, ref maximalMipLevel);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);

			this.GLTextureCache.Add(texturePath.ConvertPathSeparatorsToCurrentNativeSeparator().ToUpperInvariant(), textureID);
			return textureID;
		}

		/// <summary>
		/// Creates a cached texture for the specifed texture, using the specified path
		/// as a lookup key.
		/// </summary>
		public int CreateCachedTexture(Bitmap texture, string texturePath)
		{
			if (texture == null)
			{
				throw new ArgumentNullException(nameof(texture));
			}

			int textureID = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, textureID);
			LoadBitmapTexture(textureID, texture);

			// Use linear mipmapped filtering
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);

			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

			this.GLTextureCache.Add(texturePath.ConvertPathSeparatorsToCurrentNativeSeparator().ToUpperInvariant(), textureID);
			return textureID;
		}

		/// <summary>
		/// Creates a cached shader for the specifed shader, using the specified shader enumeration
		/// as a lookup key.
		/// </summary>
		public int CreateCachedShader(EverlookShader shader)
		{
			if (!Enum.IsDefined(typeof(EverlookShader), shader))
			{
				throw new ArgumentException("An unknown shader was passed to the rendering cache.", nameof(shader));
			}

			int vertexShaderID = GL.CreateShader(ShaderType.VertexShader);
			int fragmentShaderID = GL.CreateShader(ShaderType.FragmentShader);

			string vertexShaderSource;
			string fragmentShaderSource;

			switch (shader)
			{
				case EverlookShader.Plain2D:
				{
					vertexShaderSource = LoadShaderSource("Everlook.Content.Shaders.Adapted.PlainImage.PlainImageVertex.glsl");
					fragmentShaderSource = LoadShaderSource("Everlook.Content.Shaders.Adapted.PlainImage.PlainImageFragment.glsl");
					break;
				}
				case EverlookShader.UnlitWorldModel:
				{
					vertexShaderSource = LoadShaderSource("Everlook.Content.Shaders.Adapted.WorldModel.WorldModelVertex.glsl");
					fragmentShaderSource = LoadShaderSource("Everlook.Content.Shaders.Adapted.WorldModel.WorldModelFragment.glsl");
					break;
				}
				default:
				{
					vertexShaderSource = "";
					fragmentShaderSource = "";
					break;
				}
			}

			int result;
			int compilationLogLength;

			Console.WriteLine("Compiling vertex shader...");
			GL.ShaderSource(vertexShaderID, vertexShaderSource);
			GL.CompileShader(vertexShaderID);

			GL.GetShader(vertexShaderID, ShaderParameter.CompileStatus, out result);
			GL.GetShader(vertexShaderID, ShaderParameter.InfoLogLength, out compilationLogLength);

			if (compilationLogLength > 0)
			{
				string compilationLog;
				GL.GetShaderInfoLog(vertexShaderID, out compilationLog);

				Console.WriteLine(compilationLog);
			}

			Console.WriteLine("Compiling fragment shader...");
			GL.ShaderSource(fragmentShaderID, fragmentShaderSource);
			GL.CompileShader(fragmentShaderID);

			GL.GetShader(fragmentShaderID, ShaderParameter.CompileStatus, out result);
			GL.GetShader(fragmentShaderID, ShaderParameter.InfoLogLength, out compilationLogLength);

			if (compilationLogLength > 0)
			{
				string compilationLog;
				GL.GetShaderInfoLog(fragmentShaderID, out compilationLog);

				Console.WriteLine(compilationLog);
			}


			Console.WriteLine("Linking shader program...");
			int shaderProgramID = GL.CreateProgram();

			GL.AttachShader(shaderProgramID, vertexShaderID);
			GL.AttachShader(shaderProgramID, fragmentShaderID);
			GL.LinkProgram(shaderProgramID);

			GL.GetProgram(shaderProgramID, ProgramParameter.LinkStatus, out result);
			GL.GetProgram(shaderProgramID, ProgramParameter.InfoLogLength, out compilationLogLength);

			if (compilationLogLength > 0)
			{
				string compilationLog;
				GL.GetProgramInfoLog(shaderProgramID, out compilationLog);

				Console.WriteLine(compilationLog);
			}

			// Clean up the shader source code and unlinked object files from graphics memory
			GL.DetachShader(shaderProgramID, vertexShaderID);
			GL.DetachShader(shaderProgramID, fragmentShaderID);

			GL.DeleteShader(vertexShaderID);
			GL.DeleteShader(fragmentShaderID);


			this.GLShaderCache.Add(shader, shaderProgramID);
			return shaderProgramID;
		}

		private static string LoadShaderSource(string shaderResourcePath)
		{
			string shaderSource;
			using (Stream shaderStream =
					Assembly.GetExecutingAssembly().GetManifestResourceStream(shaderResourcePath))
			{
				if (shaderStream == null)
				{
					return null;
				}

				using (StreamReader sr = new StreamReader(shaderStream))
				{
					shaderSource = sr.ReadToEnd();
				}
			}

			return shaderSource;
		}

		private static void LoadDXTTexture(int textureID, BLP compressedImage)
		{
			GL.BindTexture(TextureTarget.Texture2D, textureID);

			// Load the set of raw compressed mipmaps
			for (uint i = 0; i < compressedImage.GetMipMapCount(); ++i)
			{
				byte[] compressedMipMap = compressedImage.GetRawMipMap(i);
				Resolution mipResolution = compressedImage.GetMipLevelResolution(i);

				PixelInternalFormat compressionFormat;
				switch (compressedImage.GetPixelFormat())
				{
					case BLPPixelFormat.Pixel_DXT1:
					{
						compressionFormat = PixelInternalFormat.CompressedRgbaS3tcDxt1Ext;
						break;
					}
					case BLPPixelFormat.Pixel_DXT3:
					{
						compressionFormat = PixelInternalFormat.CompressedRgbaS3tcDxt3Ext;
						break;
					}
					case BLPPixelFormat.Pixel_DXT5:
					{
						compressionFormat = PixelInternalFormat.CompressedRgbaS3tcDxt5Ext;
						break;
					}
					default:
					{
						throw new ArgumentException($"Image format (DXTC) did not match pixel format: {compressedImage.GetPixelFormat()}", nameof(Image));
					}
				}

				// Load the mipmap into the texture
				GL.CompressedTexImage2D(TextureTarget.Texture2D, (int)i,
					compressionFormat,
					(int)mipResolution.X,
					(int)mipResolution.Y,
					0,
					compressedMipMap.Length,
					compressedMipMap);
			}
		}

		private static void LoadBitmapTexture(int textureID, Bitmap texture)
		{
			GL.BindTexture(TextureTarget.Texture2D, textureID);

			// Extract raw RGB data from the largest bitmap
			BitmapData pixels = texture.LockBits(new Rectangle(0, 0, texture.Width, texture.Height),
			ImageLockMode.ReadOnly, SysPixelFormat.Format32bppArgb);

			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, pixels.Width, pixels.Height, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, pixels.Scan0);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

			texture.UnlockBits(pixels);
		}

		/// <summary>
		/// Disposes of the rendering cache, deleting any cached textures or shaders.
		/// </summary>
		public void Dispose()
		{
			foreach (KeyValuePair<string, int> cachedTexture in this.GLTextureCache)
			{
				GL.DeleteTexture(cachedTexture.Value);
			}

			foreach (KeyValuePair<EverlookShader, int> cachedShader in this.GLShaderCache)
			{
				GL.DeleteProgram(cachedShader.Value);
			}
		}
	}
}