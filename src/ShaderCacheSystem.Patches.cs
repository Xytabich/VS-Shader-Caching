using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using XXHash;

namespace PowerOfMind.ShaderCache
{
	public partial class ShaderCacheSystem
	{
		private static readonly Action<ShaderProgram, string, HashSet<string>> collectShaderUniformNames = null;

		static ShaderCacheSystem()
		{
			var shader = Expression.Parameter(typeof(ShaderProgram));
			var code = Expression.Parameter(typeof(string));
			var list = Expression.Parameter(typeof(HashSet<string>));
			collectShaderUniformNames = Expression.Lambda<Action<ShaderProgram, string, HashSet<string>>>(
				Expression.Call(shader, "collectUniformNames", null, code, list),
				shader, code, list
			).Compile();
		}

		private static bool CompileShaderPrefix(ShaderProgram __instance, ref bool __result, out bool __state)
		{
			__state = false;
			if(system != null && __instance.LoadFromFile)
			{
				var key = new AssetLocation(__instance.AssetDomain, __instance.PassName);
				var hashes = new List<ShaderHashInfo>(3);
				if(!CollectShaderHashes(__instance, hashes)) return true;

				if(system.HasCachedShaderProgram(key) && system.TryLoadCachedShaderProgram(key, hashes, out __instance.ProgramId, out _))
				{
					var uniformNames = new HashSet<string>();
					if(__instance.VertexShader != null)
					{
						collectShaderUniformNames(__instance, __instance.VertexShader.Code, uniformNames);
					}
					if(__instance.FragmentShader != null)
					{
						collectShaderUniformNames(__instance, __instance.FragmentShader.Code, uniformNames);
					}
					if(__instance.GeometryShader != null)
					{
						collectShaderUniformNames(__instance, __instance.GeometryShader.Code, uniformNames);
					}
					string notFoundUniforms = "";
					foreach(string uniformName in uniformNames)
					{
						__instance.uniformLocations[uniformName] = ScreenManager.Platform.GetUniformLocation(__instance, uniformName);
						if(__instance.uniformLocations[uniformName] == -1)
						{
							if(notFoundUniforms.Length > 0)
							{
								notFoundUniforms += ", ";
							}
							notFoundUniforms += uniformName;
						}
					}
					if(notFoundUniforms.Length > 0 && ScreenManager.Platform.GlDebugMode)
					{
						ScreenManager.Platform.Logger.Notification("Shader {0}: Uniform locations for variables {1} not found (or not used).", __instance.PassName, notFoundUniforms);
					}
					__state = true;
					__result = true;
					ScreenManager.Platform.Logger.Notification("Loaded cached shader program for render pass {0}.", key);
					return false;
				}
			}
			return true;
		}

		private static void CompileShaderPostfix(ShaderProgram __instance, bool __state)
		{
			if(system != null && __instance.LoadFromFile && !__state)
			{
				var hashes = new List<ShaderHashInfo>(3);
				if(!CollectShaderHashes(__instance, hashes)) return;

				var key = new AssetLocation(__instance.AssetDomain, __instance.PassName);
				if(system.SaveShaderProgramToCache(key, __instance.ProgramId, hashes))
				{
					ScreenManager.Platform.Logger.Notification("Cached shader program for render pass {0}.", key);
				}
			}
		}

		private static IEnumerable<CodeInstruction> DisposeShaderTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var insts = new List<CodeInstruction>(instructions);
			(FieldInfo field, string name)[] fields = {
				(AccessTools.Field(typeof(ShaderProgramBase), nameof(ShaderProgramBase.VertexShader)), nameof(ShaderProgramBase.VertexShader)),
				(AccessTools.Field(typeof(ShaderProgramBase), nameof(ShaderProgramBase.FragmentShader)), nameof(ShaderProgramBase.FragmentShader)),
				(AccessTools.Field(typeof(ShaderProgramBase), nameof(ShaderProgramBase.GeometryShader)), nameof(ShaderProgramBase.GeometryShader))
			};
			for(int i = 0; i < insts.Count; i++)
			{
				if(i + 2 < insts.Count)
				{
					if(insts[i].opcode == OpCodes.Ldarg_0 && (insts[i + 2].opcode == OpCodes.Brfalse || insts[i + 2].opcode == OpCodes.Brfalse_S))
					{
						foreach(var info in fields)
						{
							if(insts[i + 1].LoadsField(info.field))
							{
								insts[i + 2] = new CodeInstruction(OpCodes.Brfalse, insts[i + 2].operand);
								insts.InsertRange(i + 3, new CodeInstruction[] {
									CodeInstruction.LoadArgument(0),
									CodeInstruction.LoadField(typeof(ShaderProgramBase), info.name),
									CodeInstruction.LoadField(typeof(Shader), nameof(Shader.ShaderId)),
									new CodeInstruction(OpCodes.Brfalse, insts[i + 2].operand)
								});
								i += 7;
								break;
							}
						}
					}
				}
			}
			return insts;
		}

		private static bool CollectShaderHashes(ShaderProgramBase shader, ICollection<ShaderHashInfo> hashes)
		{
			ShaderHashInfo hash;
			if(shader.VertexShader != null)
			{
				if(!TryGetShaderHash(shader.VertexShader, out hash))
				{
					return false;
				}
				hashes.Add(hash);
			}
			if(shader.FragmentShader != null)
			{
				if(!TryGetShaderHash(shader.FragmentShader, out hash))
				{
					return false;
				}
				hashes.Add(hash);
			}
			if(shader.GeometryShader != null)
			{
				if(!TryGetShaderHash(shader.GeometryShader, out hash))
				{
					return false;
				}
				hashes.Add(hash);
			}
			return true;
		}

		private static bool TryGetShaderHash(Shader shader, out ShaderHashInfo info)
		{
			string shaderCode = shader.Code;
			if(!string.IsNullOrEmpty(shaderCode))
			{
				int approxSize = shaderCode.Length + 16;
				if(!string.IsNullOrEmpty(shader.PrefixCode)) approxSize += shader.PrefixCode.Length;
				int versionIndex = Math.Max(0, shaderCode.IndexOf("#version"));
				int newLineIndex = -1;

				var sb = new StringBuilder(approxSize);
				if(RuntimeEnv.OS == OS.Mac)
				{
					sb.Append("#version 330");
					if(versionIndex < 0)
					{
						sb.AppendLine();
					}
				}
				else if(versionIndex >= 0)
				{
					newLineIndex = shaderCode.IndexOf("\n", versionIndex);
					sb.Append(shaderCode, 0, newLineIndex + 1);
				}
				sb.Append(shader.PrefixCode);
				sb.Append(shaderCode, (newLineIndex + 1), shaderCode.Length - (newLineIndex + 1));

				ulong hash = 0;
				if(sb.Length > approxSize)
				{
					hash = XXHash64.Hash(sb.ToString());
				}
				else
				{
					foreach(var chunk in sb.GetChunks())
					{
						hash = XXHash64.Hash(chunk.Span);
						break;
					}
				}

				info = new ShaderHashInfo(shader.Type, hash, sb.Length);
				return true;
			}

			info = default;
			return false;
		}

		private static IEnumerable<CodeInstruction> CreateShaderTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var insts = new List<CodeInstruction>(instructions);
			var linkMethod = typeof(GL).GetMethod("LinkProgram", BindingFlags.Static | BindingFlags.Public, new Type[] { typeof(int) });
			for(int i = 0; i < insts.Count; i++)
			{
				if(i + 1 < insts.Count)
				{
					if(insts[i].IsLdloc() && insts[i + 1].Calls(linkMethod))
					{
						var ldarg = CodeInstruction.LoadArgument(0);
						ldarg.labels.AddRange(insts[i].labels);

						insts[i].labels.Clear();

						insts.InsertRange(i, new CodeInstruction[] {
							ldarg,
							new CodeInstruction(insts[i]),
							CodeInstruction.Call(() => SetShaderBinProgramFlag)
						});
						break;
					}
				}
			}
			return insts;
		}

		private static void SetShaderBinProgramFlag(ShaderProgram shader, int handle)
		{
			if(system != null && shader.LoadFromFile && system.HasProgramBinaryArb())
			{
				GL.ProgramParameter(handle, ProgramParameterName.ProgramBinaryRetrievableHint, (int)All.True);
			}
		}
	}
}