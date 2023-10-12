﻿using HarmonyLib;
using Newtonsoft.Json;
using OpenTK.Graphics.OpenGL;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace PowerOfMind.ShaderCache
{
	public partial class ShaderCacheSystem : ModSystem
	{
		private const byte SHADER_CACHE_VERSION = 1;

		private static ShaderCacheSystem system = null;

		private Dictionary<AssetLocation, int> cachedShaders = null;
		private Stack<int> freeEntries = null;
		private int entriesCounter = 0;

		private bool shaderCacheDirty = false;
		private HashSet<ShaderHashInfo> tmpShaderHashSet = null;

		private bool? arbGetProgramBinary = null;

		private Harmony harmony = null;
		private ICoreClientAPI api = null;

		public override void StartClientSide(ICoreClientAPI api)
		{
			system = this;

			this.api = api;

			api.Event.ReloadShader += OnReloadShaders;
			harmony = new Harmony("pomshadercache");

			harmony.Patch(typeof(ShaderProgram).GetMethod(nameof(ShaderProgram.Compile), BindingFlags.Instance | BindingFlags.Public),
				prefix: new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => CompileShaderPrefix)),
				postfix: new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => CompileShaderPostfix)));
			harmony.Patch(typeof(ShaderProgramBase).GetMethod(nameof(ShaderProgramBase.Dispose), BindingFlags.Instance | BindingFlags.Public),
				transpiler: new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => DisposeShaderTranspiler)));
			harmony.Patch(typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformWindows.CreateShaderProgram), BindingFlags.Instance | BindingFlags.Public),
				transpiler: new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => CreateShaderTranspiler)));
		}

		public override void Dispose()
		{
			if(api != null)
			{
				if(system == this) system = null;
				harmony?.UnpatchAll("pomshadercache");
				harmony = null;
				SaveShadersCache();
			}
		}

		/// <summary>
		/// Returns <see langword="true"/> if there is a cache entry for this key.
		/// </summary>
		public bool HasCachedShaderProgram(AssetLocation shaderKey)
		{
			if(cachedShaders == null) LoadShadersCache();
			return cachedShaders.ContainsKey(shaderKey);
		}

		/// <summary>
		/// Returns <see langword="true"/> if an extension for saving a binary program is available.
		/// </summary>
		public unsafe bool HasProgramBinaryArb()
		{
			if(!arbGetProgramBinary.HasValue)
			{
				int num = GL.GetInteger(GetPName.NumProgramBinaryFormats);
				arbGetProgramBinary = GL.GetError() == ErrorCode.NoError && num > 0;
			}
			return arbGetProgramBinary.Value;
		}

		/// <summary>
		/// Forces the shader cache to be saved.
		/// </summary>
		public void SaveShadersCache()
		{
			if(cachedShaders != null && shaderCacheDirty)
			{
				shaderCacheDirty = false;
				try
				{
					var path = Path.Combine(GamePaths.Cache, "pomshadercache");
					if(!Directory.Exists(path))
					{
						Directory.CreateDirectory(path);
					}
					path = Path.Combine(path, "entries.json");
					File.WriteAllText(path, JsonConvert.SerializeObject(new CacheEntries() {
						cache = cachedShaders,
						free = freeEntries,
						counter = entriesCounter
					}));
				}
				catch { }
			}
		}

		/// <summary>
		/// Tries to load a shader from the cache, returns <see langword="true"/> and the program <paramref name="handle"/> if successful, otherwise returns <see langword="false"/> and an <paramref name="error"/>
		/// </summary>
		/// <typeparam name="TEnumerable"></typeparam>
		/// <param name="shaderKey">Shader key name</param>
		/// <param name="stagesHash">An enumerated list of stage hashes that are used to determine shader changes</param>
		/// <param name="handle">OpenGL shader program id</param>
		/// <param name="error">The reason why the shader load failed</param>
		public unsafe bool TryLoadCachedShaderProgram<TEnumerable>(AssetLocation shaderKey, TEnumerable stagesHash, out int handle, out string error)
			where TEnumerable : IEnumerable<ShaderHashInfo>
		{
			if(!HasProgramBinaryArb())
			{
				handle = 0;
				error = "Not supported";
				return false;
			}

			if(cachedShaders == null) LoadShadersCache();

			handle = 0;
			error = "Cache entry not found";
			if(cachedShaders.TryGetValue(shaderKey, out var id))
			{
				string path = null;
				try
				{
					path = Path.Combine(GamePaths.Cache, "pomshadercache", (id & 255).ToString("x2"), id.ToString("x8"));

					if(File.Exists(path))
					{
						using(var stream = File.OpenRead(path))
						{
							if(stream.ReadByte() == SHADER_CACHE_VERSION)
							{
								if(tmpShaderHashSet == null) tmpShaderHashSet = new HashSet<ShaderHashInfo>();
								else tmpShaderHashSet.Clear();
								tmpShaderHashSet.UnionWith(stagesHash);

								error = "Hash mismatch";
								int stageCount = stream.ReadByte();
								if(stageCount == tmpShaderHashSet.Count)
								{
									ulong tmpLong;
									var buffSpan = new Span<byte>(&tmpLong, sizeof(ulong));
									for(int i = 0; i < stageCount; i++)
									{
										stream.Read(buffSpan);
										int size = ((int*)&tmpLong)[0];
										var type = (EnumShaderType)((int*)&tmpLong)[1];

										stream.Read(buffSpan);
										tmpShaderHashSet.Add(new ShaderHashInfo(type, tmpLong, size));
									}

									// Since the size are the same, no new elements were added, so the shader probably hasn’t changed
									if(stageCount == tmpShaderHashSet.Count)
									{
										stream.Read(buffSpan);
										int size = ((int*)&tmpLong)[0];
										int format = ((int*)&tmpLong)[1];

										handle = GL.CreateProgram();

										var bytes = ArrayPool<byte>.Shared.Rent(size);
										stream.Read(bytes, 0, size);
										GL.ProgramBinary(handle, (BinaryFormat)format, bytes, size);
										ArrayPool<byte>.Shared.Return(bytes);

										var errno = GL.GetError();
										if(errno == ErrorCode.NoError)
										{
											GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out var status);
											if(status == (int)All.True)
											{
												return true;
											}
										}

										error = errno.ToString();
									}
								}
							}
						}
					}
				}
				catch(Exception e)
				{
					error = e.Message;
				}

				if(handle != 0)
				{
					GL.DeleteProgram(handle);
					handle = 0;
				}

				try
				{
					if(!string.IsNullOrEmpty(path) && File.Exists(path))
					{
						File.Delete(path);
					}
				}
				catch { }

				freeEntries.Push(id);
				cachedShaders.Remove(shaderKey);
			}

			return false;
		}

		/// <summary>
		/// Will try to save the shader to cache, returns true if successful
		/// </summary>
		/// <typeparam name="TEnumerable"></typeparam>
		/// <param name="shaderKey">Shader key name</param>
		/// <param name="handle">OpenGL shader program id</param>
		/// <param name="stagesHash">An enumerated list of stage hashes that are used to determine shader changes</param>
		public unsafe bool SaveShaderProgramToCache<TEnumerable>(AssetLocation shaderKey, int handle, TEnumerable stagesHash)
			where TEnumerable : IEnumerable<ShaderHashInfo>
		{
			if(!HasProgramBinaryArb()) return false;

			if(tmpShaderHashSet == null) tmpShaderHashSet = new HashSet<ShaderHashInfo>();
			else tmpShaderHashSet.Clear();
			tmpShaderHashSet.UnionWith(stagesHash);
			if(tmpShaderHashSet.Count == 0) return false;

			GL.GetProgram(handle, GetProgramParameterName.ProgramBinaryRetrievableHint, out var state);
			if(GL.GetError() != ErrorCode.NoError || state != (int)All.True) return false;
			GL.GetProgram(handle, GetProgramParameterName.ProgramBinaryLength, out var size);
			if(GL.GetError() != ErrorCode.NoError || size == 0) return false;

			if(cachedShaders == null) LoadShadersCache();
			if(!cachedShaders.TryGetValue(shaderKey, out var id))
			{
				if(freeEntries.Count > 0)
				{
					id = freeEntries.Pop();
				}
				else
				{
					id = entriesCounter++;
				}
				cachedShaders[shaderKey] = id;
			}

			try
			{
				var bytes = ArrayPool<byte>.Shared.Rent(size);
				GL.GetProgramBinary(handle, size, out size, out BinaryFormat format, bytes);
				if(GL.GetError() != ErrorCode.NoError)
				{
					ArrayPool<byte>.Shared.Return(bytes);
					return false;
				}

				var path = Path.Combine(GamePaths.Cache, "pomshadercache", (id & 255).ToString("x2"));
				if(!Directory.Exists(path)) Directory.CreateDirectory(path);
				path = Path.Combine(path, id.ToString("x8"));
				using(var stream = File.OpenWrite(path))
				{
					stream.SetLength(0);
					stream.WriteByte(SHADER_CACHE_VERSION);

					ulong tmpLong;
					var buffSpan = new Span<byte>(&tmpLong, sizeof(ulong));

					stream.WriteByte((byte)tmpShaderHashSet.Count);
					foreach(var info in tmpShaderHashSet)
					{
						((int*)&tmpLong)[0] = info.Size;
						((int*)&tmpLong)[1] = (int)info.Type;
						stream.Write(buffSpan);

						tmpLong = info.Hash;
						stream.Write(buffSpan);
					}

					((int*)&tmpLong)[0] = size;
					((int*)&tmpLong)[1] = (int)format;
					stream.Write(buffSpan);

					stream.Write(bytes, 0, size);

					shaderCacheDirty = true;
				}
				ArrayPool<byte>.Shared.Return(bytes);
				return true;
			}
			catch { }
			return false;
		}

		private void LoadShadersCache()
		{
			var path = Path.Combine(GamePaths.Cache, "pomshadercache", "entries.json");
			entriesCounter = 0;
			if(File.Exists(path))
			{
				try
				{
					var entries = JsonConvert.DeserializeObject<CacheEntries>(File.ReadAllText(path));
					if(entries != null)
					{
						cachedShaders = entries.cache;
						freeEntries = entries.free;
						entriesCounter = entries.counter;
					}
				}
				catch { }
			}
			if(cachedShaders == null)
			{
				cachedShaders = new Dictionary<AssetLocation, int>();
				freeEntries = new Stack<int>();
				entriesCounter = 0;
			}
			else if(freeEntries == null)
			{
				freeEntries = new Stack<int>();
			}
		}

		private bool OnReloadShaders()
		{
			SaveShadersCache();
			return true;
		}

		private class CacheEntries
		{
			public Dictionary<AssetLocation, int> cache;
			public Stack<int> free;
			public int counter;
		}
	}
}