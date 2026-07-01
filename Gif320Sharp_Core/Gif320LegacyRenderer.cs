using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gif320Sharp_Core
{
	public sealed class Gif320LegacyRenderer
	{
		private const int ScreenWidth = 80;
		private const int ScreenHeight = 24;
		private const int CellCount = 96;
		private const int CellWidth = 15;
		private const int CellHeight = 12;
		private const int Groups = 2;
		private const int SixelsPerGroup = CellHeight / Groups;
		private const byte PackedFull = 0x3f;
		private const byte PackedEmpty = 0x00;

		private readonly byte[,,,] _display =
			new byte[ScreenWidth, ScreenHeight, Groups, CellWidth];
		private readonly byte[,] _screenMap = new byte[ScreenWidth, ScreenHeight];
		private readonly int[] _glyphX = new int[CellCount];
		private readonly int[] _glyphY = new int[CellCount];

		private int[] _gray = Array.Empty<int>();
		private int _imageWidth;
		private int _imageHeight;
		private int _top;
		private int _left;
		private int _bottom;
		private int _right;
		private int _fullValue;
		private int _halfValue;
		private float _ratio;
		private int _currentGlyph;
		private int _blackGlyph;
		private int _whiteGlyph;
		private int _lastCellsX;
		private int _lastCellsY;

		public Gif320RenderResult Render(
			Gif320Image image,
			Gif320ConversionOptions? options = null
		)
		{
			if (image == null)
			{
				throw new ArgumentNullException(nameof(image));
			}

			Gif320ConversionOptions working = (options ?? new Gif320ConversionOptions()).Clone();
			working.Validate();

			_imageWidth = image.Width;
			_imageHeight = image.Height;
			_top = 0;
			_left = 0;
			_bottom = image.Height;
			_right = image.Width;
			_fullValue = working.FullThreshold;
			_halfValue = working.HalfThreshold;
			_ratio = (float)working.Ratio;
			_gray = BuildGrayMap(image, working);

			Optimise(0, 0);

			string[] rows = BuildRows(_lastCellsX, _lastCellsY);
			IReadOnlyList<string> patterns = BuildGlyphPatterns();
			string sequence = BuildPipeSequence(_lastCellsX, _lastCellsY, patterns);
			return new Gif320RenderResult(
				sequence,
				rows,
				patterns,
				null,
				new Gif320ToneSettings
				{
					RedWeight = working.RedBalance,
					GreenWeight = working.GreenBalance,
					BlueWeight = working.BlueBalance,
					Threshold = working.FullThreshold / 100.0,
					HalfThreshold = working.HalfThreshold / 100.0,
					DitherMode = Gif320DitherMode.Checkerboard,
				},
				_lastCellsX,
				_lastCellsY,
				_currentGlyph,
				0.0,
				0.0,
				0.0,
				0.0
			);
		}

		private static int[] BuildGrayMap(
			Gif320Image image,
			Gif320ConversionOptions options
		)
		{
			int lr = options.RedBalance * (0x100 / 100);
			int lg = options.GreenBalance * (0x100 / 100);
			int lb = options.BlueBalance * (0x100 / 100);
			var gray = new int[image.Width * image.Height];
			for (int i = 0, pixel = 0; i < gray.Length; i++, pixel += 3)
			{
				gray[i] = (
					lr * image.RgbPixels[pixel]
					+ lg * image.RgbPixels[pixel + 1]
					+ lb * image.RgbPixels[pixel + 2]
				) >> 8;
			}

			return gray;
		}

		private void Optimise(int maxX, int maxY)
		{
			float minX;
			float minY;
			float diffX;
			float diffY;
			bool lastWorked;

			float rat = _ratio * (ScreenWidth / (float)ScreenHeight);
			float target = CellCount;
			float chrs = CellCount;
			do
			{
				minY = Msqrt(chrs / rat);
				minX = rat * minY;
				chrs -= 0.1f;
			}
			while (minY * minX > target);

			if (maxX != 0 && maxY != 0)
			{
				if (maxX < minX || maxY < minY)
				{
					minX = 2.0f;
					minY = 2.0f;
				}

				diffX = maxX - minX;
				diffY = maxY - minY;
				if (diffX > ScreenWidth - minX)
				{
					diffY *= (ScreenWidth - minX) / diffX;
					diffX = ScreenWidth - minX;
				}

				if (diffY > ScreenHeight - minY)
				{
					diffX *= (ScreenHeight - minY) / diffY;
					diffY = ScreenHeight - minY;
				}
			}
			else
			{
				diffX = (24.0f * rat) - minX;
				if (diffX > ScreenWidth - minX)
				{
					diffY = (ScreenWidth / rat) - minY;
					diffX = ScreenWidth - minX;
				}
				else
				{
					diffY = ScreenHeight - minY;
				}
			}

			ClearImage();
			if (Develop(
				((int)(minX + diffX)) * CellWidth,
				((int)(minY + diffY)) * CellHeight
			))
			{
				diffX /= 2.0f;
				diffY /= 2.0f;
				do
				{
					ClearImage();
					if (!Develop(
						((int)(minX + diffX)) * CellWidth,
						((int)(minY + diffY)) * CellHeight
					))
					{
						lastWorked = true;
						minX += diffX;
						minY += diffY;
					}
					else
					{
						lastWorked = false;
					}

					diffX /= 2.0f;
					diffY /= 2.0f;
				}
				while (diffX > 1.0f || diffY > 1.0f || !lastWorked);
			}
			else
			{
				minX += diffX;
				minY += diffY;
			}

			_lastCellsX = (int)minX;
			_lastCellsY = (int)minY;
		}

		private bool Develop(int pixelWidth, int pixelHeight)
		{
			int cellsWidth = pixelWidth / CellWidth;
			int cellsHeight = pixelHeight / CellHeight;
			float tyRatio = (_bottom - _top) / (float)pixelHeight;
			float txRatio = (_right - _left) / (float)pixelWidth;
			int hexFull = (_fullValue * 0x100) / 100;
			int hexHalf = (_halfValue * 0x100) / 100;
			PackInit();

			for (int cellY = cellsHeight - 1; cellY >= 0; cellY--)
			{
				for (int cellX = cellsWidth - 1; cellX >= 0; cellX--)
				{
					int endPixelX = (cellX + 1) * CellWidth;
					int endPixelY = (cellY + 1) * CellHeight;
					for (int pixelY = cellY * CellHeight; pixelY < endPixelY; pixelY++)
					{
						int ditherHalf = (cellX + pixelY) % 2;
						int ditherFull = ditherHalf ^ 1;
						for (int pixelX = cellX * CellWidth; pixelX < endPixelX; pixelX += 2)
						{
							long sum = 0;
							long area = 0;
							int sourceStartX = (int)(pixelX * txRatio + _left);
							int sourceEndX = (int)((pixelX + 2) * txRatio + _left);
							sourceStartX = ClampInt(sourceStartX, 0, _imageWidth - 1);
							sourceEndX = ClampInt(sourceEndX, 0, _imageWidth - 1);

							int sourceStartY = (int)(pixelY * tyRatio + _top);
							int sourceEndY = (int)((pixelY + 1) * tyRatio + _top);
							sourceStartY = ClampInt(sourceStartY, 0, _imageHeight - 1);
							sourceEndY = ClampInt(sourceEndY, 0, _imageHeight - 1);

							for (int sourceY = sourceStartY; sourceY <= sourceEndY; sourceY++)
							{
								int rowOffset = sourceY * _imageWidth;
								int start = rowOffset + sourceStartX;
								int end = rowOffset + sourceEndX;
								for (int source = start; source <= end; source++)
								{
									sum += _gray[source];
								}

								area += (end - start) + 1;
							}

							if (area != 0)
							{
								sum /= area;
							}
							else
							{
								sum = _gray[sourceStartX];
							}

							if (sum >= hexFull)
							{
								Plot(ditherFull + pixelX, pixelY);
							}

							if (sum >= hexHalf)
							{
								Plot(ditherHalf + pixelX, pixelY);
							}
						}
					}

					if (Pack(cellY, cellX))
					{
						return true;
					}
				}
			}

			return false;
		}

		private void Plot(int x, int y)
		{
			if (x < 0
				|| y < 0
				|| x >= ScreenWidth * CellWidth
				|| y >= ScreenHeight * CellHeight)
			{
				return;
			}

			int cellX = x / CellWidth;
			int slice = x % CellWidth;
			int cellY = y / CellHeight;
			int localY = y % CellHeight;
			int group = localY / SixelsPerGroup;
			int sixel = localY % SixelsPerGroup;
			_display[cellX, cellY, group, slice] |= (byte)(1 << sixel);
		}

		private bool Pack(int cellY, int cellX)
		{
			if (CellMatches(cellX, cellY, PackedFull))
			{
				if (_blackGlyph < 0)
				{
					_blackGlyph = _currentGlyph;
				}
				else
				{
					_screenMap[cellX, cellY] = (byte)_blackGlyph;
					return false;
				}
			}
			else if (CellMatches(cellX, cellY, PackedEmpty))
			{
				if (_whiteGlyph < 0)
				{
					_whiteGlyph = _currentGlyph;
				}
				else
				{
					_screenMap[cellX, cellY] = (byte)_whiteGlyph;
					return false;
				}
			}

			_screenMap[cellX, cellY] = (byte)_currentGlyph;
			_glyphX[_currentGlyph] = cellX;
			_glyphY[_currentGlyph] = cellY;
			_currentGlyph++;
			return _currentGlyph >= CellCount;
		}

		private bool CellMatches(int cellX, int cellY, byte value)
		{
			for (int group = 0; group < Groups; group++)
			{
				for (int slice = 0; slice < CellWidth; slice++)
				{
					if (_display[cellX, cellY, group, slice] != value)
					{
						return false;
					}
				}
			}

			return true;
		}

		private void PackInit()
		{
			_currentGlyph = 0;
			_blackGlyph = -1;
			_whiteGlyph = -1;
		}

		private void ClearImage()
		{
			Array.Clear(_display, 0, _display.Length);
			Array.Clear(_screenMap, 0, _screenMap.Length);
			Array.Clear(_glyphX, 0, _glyphX.Length);
			Array.Clear(_glyphY, 0, _glyphY.Length);
		}

		private string[] BuildRows(int cellsX, int cellsY)
		{
			var rows = new string[cellsY];
			for (int y = 0; y < cellsY; y++)
			{
				var chars = new char[cellsX];
				for (int x = 0; x < cellsX; x++)
				{
					chars[x] = (char)(' ' + _screenMap[x, y]);
				}

				rows[y] = new string(chars);
			}

			return rows;
		}

		private IReadOnlyList<string> BuildGlyphPatterns()
		{
			var patterns = new string[_currentGlyph];
			for (int i = 0; i < _currentGlyph; i++)
			{
				patterns[i] = BuildGlyphPattern(_glyphX[i], _glyphY[i]);
			}

			return patterns;
		}

		private string BuildGlyphPattern(int cellX, int cellY)
		{
			var builder = new StringBuilder((CellWidth * Groups) + 1);
			for (int group = 0; group < Groups; group++)
			{
				for (int slice = 0; slice < CellWidth; slice++)
				{
					builder.Append((char)(_display[cellX, cellY, group, slice] + 0x3f));
				}

				if (group == 0)
				{
					builder.Append('/');
				}
			}

			return builder.ToString();
		}

		private string BuildPipeSequence(
			int cellsX,
			int cellsY,
			IReadOnlyList<string> patterns
		)
		{
			var builder = new StringBuilder();

			builder.Append("\u001b(0");
			builder.Append("\u001b(B");
			builder.Append("\u001bP1;0;0;15;1;2;12;1{ @");
			builder.Append('\n');
			foreach (string pattern in patterns)
			{
				builder.Append(pattern);
				builder.Append(";\n");
			}

			builder.Append("\u001b\\");
			builder.Append("\u001b) @");
			builder.Append('\u000e');
			builder.Append("\u001b[1m");
			builder.Append('\n');

			for (int y = 0; y < cellsY; y++)
			{
				AppendCursorMove(builder, 2 + y, (ScreenWidth - cellsX) / 2);
				for (int x = 0; x < cellsX; x++)
				{
					builder.Append((char)(' ' + _screenMap[x, y]));
				}

				builder.Append('\n');
			}

			builder.Append('\u000f');
			builder.Append("\u001b[22m");
			builder.Append('\u000f');
			builder.Append("\u001b[22m");
			return builder.ToString();
		}

		private static void AppendCursorMove(
			StringBuilder builder,
			int row,
			int column
		)
		{
			builder.Append("\u001b[");
			builder.Append(row.ToString(CultureInfo.InvariantCulture));
			builder.Append(';');
			builder.Append(column.ToString(CultureInfo.InvariantCulture));
			builder.Append('f');
		}

		private static float Msqrt(float x)
		{
			float y = 2.0f;
			float q = -1.0f;
			float lastq;
			do
			{
				lastq = q;
				q = x / y;
				y = (q + y) / 2.0f;
			}
			while (q != lastq);

			return q;
		}

		private static int ClampInt(int value, int min, int max)
		{
			if (value < min)
			{
				return min;
			}

			if (value > max)
			{
				return max;
			}

			return value;
		}
	}
}
