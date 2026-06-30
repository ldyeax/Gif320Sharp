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
