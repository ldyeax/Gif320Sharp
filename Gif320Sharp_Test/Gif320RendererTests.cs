using System;
using System.Linq;
using Gif320Sharp_Core;

namespace Gif320Sharp_Test
{
	[TestClass]
	public sealed class Gif320RendererTests
	{
		[TestMethod]
		public void FullScreenDoubleUsesFortyByTwelveCells()
		{
			byte[] image = CreateGradient(96, 48);
			var renderer = new Gif320Renderer();

			Gif320RenderResult result = renderer.RenderRgb(
				image,
				96,
				48,
				Gif320RenderOptions.FullScreenDouble()
			);

			Assert.AreEqual(40, result.CellsX);
			Assert.AreEqual(12, result.CellsY);
			Assert.AreEqual(12, result.ScreenRows.Length);
			Assert.IsTrue(result.ScreenRows.All(row => row.Length == 40));
			Assert.IsTrue(result.GlyphCount <= 94);
			Assert.IsTrue(result.VtSequence.Contains("\u001b#3"));
			Assert.IsTrue(result.VtSequence.Contains("\u001b#4"));
		}

		[TestMethod]
		public void FullScreenDoubleLeavesDrcsDesignatedForG1()
		{
			byte[] image = CreateGradient(96, 48);
			var renderer = new Gif320Renderer();

			Gif320RenderResult result = renderer.RenderRgb(
				image,
				96,
				48,
				Gif320RenderOptions.FullScreenDouble()
			);

			StringAssert.Contains(result.VtSequence, "\u001b) @");
			Assert.IsFalse(
				result.VtSequence.Contains("\u001b)B"),
				"Restoring G1 to ASCII makes already-written DRCS cells display as literal punctuation in consumers that track G1 at render time."
			);
			Assert.IsTrue(result.VtSequence.EndsWith("\u001b(B", StringComparison.Ordinal));
		}

		[TestMethod]
		public void VectorQuantizationCapsGlyphCountWhenCellsAreUnique()
		{
			byte[] image = CreateNoisyImage(240, 72);
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 16,
				CellsY = 6,
				MaxGlyphs = 12,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				ToneSettings = new Gif320ToneSettings
				{
					Threshold = 0.5,
					DitherMode = Gif320DitherMode.Threshold,
				},
			};

			Gif320RenderResult result = renderer.RenderRgb(image, 240, 72, options);

			Assert.IsTrue(result.UniqueGlyphCountBeforeReduction > 12);
			Assert.IsTrue(result.WasGlyphReduced);
			Assert.IsTrue(result.GlyphCount <= 12);
			Assert.IsTrue(result.ReductionErrorPerCellPixel >= 0.0);
			Assert.IsTrue(result.HighReductionErrorPerCellPixel >= 0.0);
			Assert.IsTrue(result.HighReductionErrorPerCellPixel <= result.WorstReductionErrorPerCellPixel);
			Assert.IsTrue(result.WorstReductionErrorPerCellPixel <= 1.0);
		}

		[TestMethod]
		public void FullBrightCellUsesReverseVideoSpace()
		{
			byte[] image = CreateSolidImage(
				Gif320RenderOptions.CellPixelWidth,
				Gif320RenderOptions.CellPixelHeight,
				255
			);
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 1,
				CellsY = 1,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				ToneSettings = new Gif320ToneSettings
				{
					Threshold = 0.5,
					DitherMode = Gif320DitherMode.Threshold,
				},
			};

			Gif320RenderResult result = renderer.RenderRgb(
				image,
				Gif320RenderOptions.CellPixelWidth,
				Gif320RenderOptions.CellPixelHeight,
				options
			);

			Assert.AreEqual(0, result.GlyphCount);
			Assert.AreEqual(" ", result.ScreenRows[0]);
			Assert.IsTrue(result.ReverseVideoCells[0]);
			StringAssert.Contains(result.VtSequence, "\u001b[7m \u001b[27m");
		}

		[TestMethod]
		public void NearInvertedCellsReuseOneGlyphWithReverseVideo()
		{
			byte[] image = CreateInvertedPairImage();
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 2,
				CellsY = 1,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				ReverseVideoInversionTolerance = 1,
				ToneSettings = new Gif320ToneSettings
				{
					Threshold = 0.5,
					DitherMode = Gif320DitherMode.Threshold,
				},
			};

			Gif320RenderResult result = renderer.RenderRgb(
				image,
				Gif320RenderOptions.CellPixelWidth * 2,
				Gif320RenderOptions.CellPixelHeight,
				options
			);

			Assert.AreEqual(1, result.GlyphCount);
			Assert.AreEqual("!!", result.ScreenRows[0]);
			Assert.IsFalse(result.ReverseVideoCells[0]);
			Assert.IsTrue(result.ReverseVideoCells[1]);
			StringAssert.Contains(result.VtSequence, "!\u001b[7m!\u001b[27m");
		}

		[TestMethod]
		public void ManualAtlasOverrideReplacesGlyphPixels()
		{
			byte[] image = CreateSingleStrokeCell();
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 1,
				CellsY = 1,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				ToneSettings = new Gif320ToneSettings
				{
					Threshold = 0.5,
					DitherMode = Gif320DitherMode.Threshold,
				},
			};

			Gif320RenderResult generated = renderer.RenderRgb(
				image,
				Gif320RenderOptions.CellPixelWidth,
				Gif320RenderOptions.CellPixelHeight,
				options
			);
			options.ManualAtlas = "gif320-atlas-v1:" + new string('0', 46);

			Gif320RenderResult manual = renderer.RenderRgb(
				image,
				Gif320RenderOptions.CellPixelWidth,
				Gif320RenderOptions.CellPixelHeight,
				options
			);

			string blankSixel = new string('?', Gif320RenderOptions.CellPixelWidth)
				+ "/"
				+ new string('?', Gif320RenderOptions.CellPixelWidth);
			Assert.AreEqual(1, generated.GlyphCount);
			Assert.AreEqual(1, manual.GlyphCount);
			Assert.AreEqual("!", manual.ScreenRows[0]);
			Assert.AreNotEqual(generated.GlyphSixelPatterns[0], manual.GlyphSixelPatterns[0]);
			Assert.AreEqual(blankSixel, manual.GlyphSixelPatterns[0]);
			Assert.AreEqual(options.ManualAtlas, manual.GlyphAtlas);
		}

		[TestMethod]
		public void ManualCellMapKeepsAtlasSlotsStable()
		{
			int width = Gif320RenderOptions.CellPixelWidth * 2;
			int height = Gif320RenderOptions.CellPixelHeight;
			byte[] image = CreateSolidImage(width, height, 0);
			var renderer = new Gif320Renderer();
			string blankGlyph = new string('0', 46);
			string fullGlyph = new string('f', 44) + "0f";
			var options = new Gif320RenderOptions
			{
				CellsX = 2,
				CellsY = 1,
				AutoTune = true,
				MaxGlyphs = 2,
				ResizeMode = Gif320ResizeMode.Stretch,
				ManualAtlas = $"gif320-atlas-v1:{blankGlyph},{fullGlyph}",
				ManualCellMap = "gif320-map-v1:2x1:0201",
			};

			Gif320RenderResult result = renderer.RenderRgb(image, width, height, options);

			Assert.AreEqual("\"!", result.ScreenRows[0]);
			Assert.AreEqual(options.ManualAtlas, result.GlyphAtlas);
			Assert.AreEqual(options.ManualCellMap, result.CellMap);
		}

		[TestMethod]
		public void AutomaticSettingsReturnScoredToneSettings()
		{
			byte[] image = CreateGradient(64, 64);
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 8,
				CellsY = 4,
				MaxGlyphs = 32,
				AutoTune = true,
				AutoTuneFinalists = 4,
			};

			Gif320RenderResult result = renderer.RenderRgb(image, 64, 64, options);

			Assert.IsTrue(result.Score > 0.0);
			Assert.IsTrue(result.ToneSettings.Threshold > 0.0);
			Assert.IsTrue(result.ToneSettings.Threshold < 1.0);
			Assert.IsTrue(result.GlyphCount <= 32);
			Assert.IsFalse(string.IsNullOrEmpty(result.VtSequence));
		}

		[TestMethod]
		public void AutoTuneLocksKeepConfiguredToneValues()
		{
			byte[] image = CreateGradient(64, 64);
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 8,
				CellsY = 4,
				MaxGlyphs = 32,
				AutoTune = true,
				AutoTuneFinalists = 4,
				AutoTuneLocks = Gif320AutoTuneLocks.Tone,
				ToneSettings = new Gif320ToneSettings
				{
					RedWeight = 9.0,
					GreenWeight = 3.0,
					BlueWeight = 0.0,
					Threshold = 0.62,
					HalfThreshold = 0.31,
					DitherMode = Gif320DitherMode.Checkerboard,
				},
			};

			Gif320RenderResult result = renderer.RenderRgb(image, 64, 64, options);

			Assert.AreEqual(0.75, result.ToneSettings.RedWeight, 0.000001);
			Assert.AreEqual(0.25, result.ToneSettings.GreenWeight, 0.000001);
			Assert.AreEqual(0.0, result.ToneSettings.BlueWeight, 0.000001);
			Assert.AreEqual(0.62, result.ToneSettings.Threshold, 0.000001);
			Assert.AreEqual(0.31, result.ToneSettings.HalfThreshold, 0.000001);
		}

		[TestMethod]
		public void ConverterCanDeriveCellHeightFromConfiguredWidth()
		{
			byte[] pixels = CreateGradient(160, 90);
			var image = new Gif320Image(160, 90, pixels, colorCount: 0);
			var converter = new Gif320Converter();
			var options = new Gif320ConversionOptions
			{
				CellsX = 20,
				DeriveCellsYFromX = true,
				OptimizeSize = false,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				DitherMode = Gif320DitherMode.Threshold,
			};

			Gif320RenderResult result = converter.Render(image, options);

			Assert.AreEqual(20, result.CellsX);
			Assert.AreEqual(4, result.CellsY);
		}

		[TestMethod]
		public void ConverterCanDeriveCellWidthFromConfiguredHeightUsingDisplayedAspect()
		{
			byte[] pixels = CreateGradient(100, 100);
			var image = new Gif320Image(100, 100, pixels, colorCount: 0);
			var converter = new Gif320Converter();
			var options = new Gif320ConversionOptions
			{
				CellsY = 24,
				DeriveCellsXFromY = true,
				OptimizeSize = false,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Stretch,
				DitherMode = Gif320DitherMode.Threshold,
			};

			Gif320RenderResult result = converter.Render(image, options);

			Assert.AreEqual(66, result.CellsX);
			Assert.AreEqual(24, result.CellsY);
		}

		[TestMethod]
		public void CoverResizeUsesDisplayedVt320AspectWhenSampling()
		{
			byte[] pixels = CreateHorizontalEdgeStrips(100, 100, stripHeight: 10);
			var renderer = new Gif320Renderer();
			var options = new Gif320RenderOptions
			{
				CellsX = 66,
				CellsY = 24,
				MaxGlyphs = 94,
				AutoTune = false,
				ResizeMode = Gif320ResizeMode.Cover,
				ToneSettings = new Gif320ToneSettings
				{
					Threshold = 0.5,
					DitherMode = Gif320DitherMode.Threshold,
				},
			};

			Gif320RenderResult result = renderer.RenderRgb(pixels, 100, 100, options);

			Assert.IsTrue(
				result.ReverseVideoCells.Take(result.CellsX).All(value => value),
				"The top source strip should remain visible instead of being cropped away by square-pixel target sampling."
			);
			Assert.IsTrue(
				result.ReverseVideoCells
					.Skip((result.CellsY - 1) * result.CellsX)
					.Take(result.CellsX)
					.All(value => value),
				"The bottom source strip should remain visible instead of being cropped away by square-pixel target sampling."
			);
		}

		private static byte[] CreateGradient(int width, int height)
		{
			var pixels = new byte[width * height * 3];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int offset = (y * width + x) * 3;
					pixels[offset] = (byte)(x * 255 / Math.Max(1, width - 1));
					pixels[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
					pixels[offset + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
				}
			}

			return pixels;
		}

		private static byte[] CreateHorizontalEdgeStrips(int width, int height, int stripHeight)
		{
			var pixels = new byte[width * height * 3];
			for (int y = 0; y < height; y++)
			{
				byte value = y < stripHeight || y >= height - stripHeight
					? (byte)255
					: (byte)0;
				for (int x = 0; x < width; x++)
				{
					SetPixel(pixels, width, x, y, value);
				}
			}

			return pixels;
		}

		private static byte[] CreateSolidImage(int width, int height, byte value)
		{
			var pixels = new byte[width * height * 3];
			Array.Fill(pixels, value);
			return pixels;
		}

		private static byte[] CreateInvertedPairImage()
		{
			int cellWidth = Gif320RenderOptions.CellPixelWidth;
			int cellHeight = Gif320RenderOptions.CellPixelHeight;
			int width = cellWidth * 2;
			var pixels = new byte[width * cellHeight * 3];
			for (int y = 0; y < cellHeight; y++)
			{
				for (int x = 0; x < cellWidth; x++)
				{
					bool leftOn = x == 3 || y == 5 || (x == y && x < cellHeight);
					bool rightOn = !leftOn;
					if (x == 0 && y == 0)
					{
						rightOn = leftOn;
					}

					SetPixel(pixels, width, x, y, leftOn ? (byte)255 : (byte)0);
					SetPixel(pixels, width, x + cellWidth, y, rightOn ? (byte)255 : (byte)0);
				}
			}

			return pixels;
		}

		private static byte[] CreateSingleStrokeCell()
		{
			int width = Gif320RenderOptions.CellPixelWidth;
			int height = Gif320RenderOptions.CellPixelHeight;
			var pixels = new byte[width * height * 3];
			for (int y = 0; y < height; y++)
			{
				SetPixel(pixels, width, width / 2, y, 255);
			}

			return pixels;
		}

		private static void SetPixel(byte[] pixels, int width, int x, int y, byte value)
		{
			int offset = (y * width + x) * 3;
			pixels[offset] = value;
			pixels[offset + 1] = value;
			pixels[offset + 2] = value;
		}

		private static byte[] CreateNoisyImage(int width, int height)
		{
			var pixels = new byte[width * height * 3];
			uint state = 0x12345678;
			for (int i = 0; i < width * height; i++)
			{
				state = state * 1664525u + 1013904223u;
				byte value = (byte)(state >> 24);
				int offset = i * 3;
				pixels[offset] = value;
				pixels[offset + 1] = value;
				pixels[offset + 2] = value;
			}

			return pixels;
		}
	}
}
