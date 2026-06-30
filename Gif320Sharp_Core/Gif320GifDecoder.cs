using System;
using System.Collections.Generic;
using System.IO;

namespace Gif320Sharp_Core
{
	public static class Gif320GifDecoder
	{
		public static Gif320Image DecodeFile(string path)
		{
			if (path == null)
			{
				throw new ArgumentNullException(nameof(path));
			}

			using FileStream stream = File.OpenRead(path);
			return Decode(stream);
		}

		public static Gif320Image Decode(Stream stream)
		{
			if (stream == null)
			{
				throw new ArgumentNullException(nameof(stream));
			}

			using var reader = new BinaryReader(stream);
			string signature = new string(reader.ReadChars(6));
			if (signature != "GIF87a" && signature != "GIF89a")
			{
				throw new Gif320Exception("file is not a GIF file");
			}

			int screenWidth = ReadUInt16(reader);
			int screenHeight = ReadUInt16(reader);
			byte packed = reader.ReadByte();
			bool hasGlobalColorTable = (packed & 0x80) != 0;
			int globalColorCount = 1 << ((packed & 0x07) + 1);
			int backgroundIndex = reader.ReadByte();
			reader.ReadByte();

			byte[]? globalColorTable = hasGlobalColorTable
				? reader.ReadBytes(globalColorCount * 3)
				: null;
			if (hasGlobalColorTable && globalColorTable!.Length != globalColorCount * 3)
			{
				throw new Gif320Exception("truncated global color table");
			}

			int transparentIndex = -1;
			while (true)
			{
				int blockType = reader.Read();
				if (blockType < 0)
				{
					throw new Gif320Exception("unexpected EOF before GIF trailer");
				}

				switch (blockType)
				{
					case 0x00:
						break;
					case 0x21:
						ReadExtension(reader, ref transparentIndex);
						break;
					case 0x2c:
						return ReadImage(
							reader,
							screenWidth,
							screenHeight,
							globalColorTable,
							globalColorCount,
							backgroundIndex,
							transparentIndex
						);
					case 0x3b:
						throw new Gif320Exception("GIF contains no image descriptor");
					default:
						throw new Gif320Exception("illegal GIF block type");
				}
			}
		}

		private static Gif320Image ReadImage(
			BinaryReader reader,
			int screenWidth,
			int screenHeight,
			byte[]? globalColorTable,
			int globalColorCount,
			int backgroundIndex,
			int transparentIndex
		)
		{
			_ = screenWidth;
			_ = screenHeight;
			_ = backgroundIndex;

			int left = ReadUInt16(reader);
			int top = ReadUInt16(reader);
			int width = ReadUInt16(reader);
			int height = ReadUInt16(reader);
			byte packed = reader.ReadByte();
			bool hasLocalColorTable = (packed & 0x80) != 0;
			bool isInterlaced = (packed & 0x40) != 0;
			int localColorCount = 1 << ((packed & 0x07) + 1);
			byte[]? colorTable = hasLocalColorTable
				? reader.ReadBytes(localColorCount * 3)
				: globalColorTable;
			int colorCount = hasLocalColorTable ? localColorCount : globalColorCount;

			if (colorTable == null)
			{
				throw new Gif320Exception("no colormap present for image");
			}

			if (colorTable.Length != colorCount * 3)
			{
				throw new Gif320Exception("truncated local color table");
			}

			int minCodeSize = reader.ReadByte();
			byte[] compressed = ReadSubBlocks(reader);
			byte[] indexes = DecodeLzw(compressed, minCodeSize, width * height);
			if (isInterlaced)
			{
				indexes = Deinterlace(indexes, width, height);
			}

			var rgb = new byte[width * height * 3];
			for (int i = 0; i < width * height; i++)
			{
				int colorIndex = indexes[i];
				int source = colorIndex * 3;
				int target = i * 3;
				if (colorIndex == transparentIndex || source + 2 >= colorTable.Length)
				{
					rgb[target] = 0;
					rgb[target + 1] = 0;
					rgb[target + 2] = 0;
				}
				else
				{
					rgb[target] = colorTable[source];
					rgb[target + 1] = colorTable[source + 1];
					rgb[target + 2] = colorTable[source + 2];
				}
			}

			_ = left;
			_ = top;
			return new Gif320Image(width, height, rgb, colorCount);
		}

		private static void ReadExtension(
			BinaryReader reader,
			ref int transparentIndex
		)
		{
			int label = reader.ReadByte();
			if (label == 0xf9)
			{
				int blockSize = reader.ReadByte();
				if (blockSize != 4)
				{
					reader.ReadBytes(blockSize);
					SkipDataSubBlocks(reader);
					return;
				}

				byte packed = reader.ReadByte();
				ReadUInt16(reader);
				int index = reader.ReadByte();
				reader.ReadByte();
				transparentIndex = (packed & 0x01) != 0 ? index : -1;
				return;
			}

			SkipDataSubBlocks(reader);
		}

		private static byte[] ReadSubBlocks(BinaryReader reader)
		{
			using var output = new MemoryStream();
			while (true)
			{
				int count = reader.ReadByte();
				if (count == 0)
				{
					return output.ToArray();
				}

				byte[] buffer = reader.ReadBytes(count);
				if (buffer.Length != count)
				{
					throw new Gif320Exception("truncated GIF data sub-block");
				}

				output.Write(buffer, 0, buffer.Length);
			}
		}

		private static void SkipDataSubBlocks(BinaryReader reader)
		{
			while (true)
			{
				int count = reader.ReadByte();
				if (count == 0)
				{
					return;
				}

				if (reader.ReadBytes(count).Length != count)
				{
					throw new Gif320Exception("truncated GIF extension block");
				}
			}
		}

		private static byte[] DecodeLzw(
			byte[] data,
			int minCodeSize,
			int expectedPixels
		)
		{
			if (minCodeSize < 2 || minCodeSize > 8)
			{
				throw new Gif320Exception("unsupported GIF LZW code size");
			}

			int clearCode = 1 << minCodeSize;
			int endCode = clearCode + 1;
			int codeSize = minCodeSize + 1;
			int bitOffset = 0;
			var dictionary = CreateInitialDictionary(clearCode);
			byte[]? previous = null;
			var output = new List<byte>(expectedPixels);

			while (true)
			{
				int code = ReadCode(data, bitOffset, codeSize);
				if (code < 0)
				{
					break;
				}

				bitOffset += codeSize;
				if (code == clearCode)
				{
					dictionary = CreateInitialDictionary(clearCode);
					codeSize = minCodeSize + 1;
					previous = null;
					continue;
				}

				if (code == endCode)
				{
					break;
				}

				byte[] entry;
				if (code < dictionary.Count)
				{
					entry = dictionary[code];
				}
				else if (code == dictionary.Count && previous != null)
				{
					entry = AppendFirst(previous, previous);
				}
				else
				{
					throw new Gif320Exception("illegal code in GIF raster data");
				}

				output.AddRange(entry);
				if (previous != null && dictionary.Count < 4096)
				{
					dictionary.Add(AppendFirst(previous, entry));
					if (dictionary.Count == (1 << codeSize) && codeSize < 12)
					{
						codeSize++;
					}
				}

				previous = entry;
				if (output.Count >= expectedPixels)
				{
					break;
				}
			}

			if (output.Count < expectedPixels)
			{
				throw new Gif320Exception("raster has the wrong size");
			}

			if (output.Count == expectedPixels)
			{
				return output.ToArray();
			}

			byte[] trimmed = new byte[expectedPixels];
			output.CopyTo(0, trimmed, 0, expectedPixels);
			return trimmed;
		}

		private static List<byte[]> CreateInitialDictionary(int clearCode)
		{
			var dictionary = new List<byte[]>(4096);
			for (int i = 0; i < clearCode; i++)
			{
				dictionary.Add(new[] { (byte)i });
			}

			dictionary.Add(Array.Empty<byte>());
			dictionary.Add(Array.Empty<byte>());
			return dictionary;
		}

		private static byte[] AppendFirst(byte[] prefix, byte[] suffixSource)
		{
			var result = new byte[prefix.Length + 1];
			Array.Copy(prefix, result, prefix.Length);
			result[result.Length - 1] = suffixSource[0];
			return result;
		}

		private static int ReadCode(byte[] data, int bitOffset, int codeSize)
		{
			int byteOffset = bitOffset / 8;
			int shift = bitOffset % 8;
			if (byteOffset >= data.Length)
			{
				return -1;
			}

			int value = 0;
			int availableBytes = Math.Min(3, data.Length - byteOffset);
			for (int i = 0; i < availableBytes; i++)
			{
				value |= data[byteOffset + i] << (8 * i);
			}

			value >>= shift;
			int mask = (1 << codeSize) - 1;
			if (bitOffset + codeSize > data.Length * 8)
			{
				return -1;
			}

			return value & mask;
		}

		private static byte[] Deinterlace(byte[] source, int width, int height)
		{
			var result = new byte[source.Length];
			int sourceRow = 0;
			CopyPass(0, 8);
			CopyPass(4, 8);
			CopyPass(2, 4);
			CopyPass(1, 2);
			return result;

			void CopyPass(int firstRow, int rowStep)
			{
				for (int y = firstRow; y < height; y += rowStep)
				{
					Array.Copy(
						source,
						sourceRow * width,
						result,
						y * width,
						width
					);
					sourceRow++;
				}
			}
		}

		private static int ReadUInt16(BinaryReader reader)
		{
			int low = reader.ReadByte();
			int high = reader.ReadByte();
			return low | (high << 8);
		}
	}
}
