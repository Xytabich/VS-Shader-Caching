using System;
using Vintagestory.API.Client;

namespace PowerOfMind.ShaderCache
{
	public readonly struct ShaderHashInfo : IEquatable<ShaderHashInfo>
	{
		/// <summary>
		/// Shader stage type
		/// </summary>
		public readonly EnumShaderType Type;
		/// <summary>
		/// Size of the shader source code
		/// </summary>
		public readonly int Size;
		/// <summary>
		/// Hash of the shader source code
		/// </summary>
		public readonly ulong Hash;

		public ShaderHashInfo(EnumShaderType type, ulong hash, int size)
		{
			this.Type = type;
			this.Hash = hash;
			this.Size = size;
		}

		public override bool Equals(object obj)
		{
			return obj is ShaderHashInfo info && Equals(info);
		}

		public bool Equals(ShaderHashInfo other)
		{
			return Type == other.Type &&
				   Hash == other.Hash &&
				   Size == other.Size;
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Type, Hash, Size);
		}
	}
}