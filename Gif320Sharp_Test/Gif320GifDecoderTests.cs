using System;
using System.IO;
using Gif320Sharp_Core;

namespace Gif320Sharp_Test
{
	[TestClass]
	public sealed class Gif320GifDecoderTests
	{
		private const string OnePixelGif =
			"R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";

		[TestMethod]
		public void DecodesGifToRgbImage()
		{
			byte[] bytes = Convert.FromBase64String(OnePixelGif);
			using var stream = new MemoryStream(bytes);

			Gif320Image image = Gif320GifDecoder.Decode(stream);

			Assert.AreEqual(1, image.Width);
			Assert.AreEqual(1, image.Height);
			Assert.AreEqual(3, image.RgbPixels.Length);
			Assert.AreEqual(2, image.ColorCount);
		}

		[TestMethod]
		public void ConverterRendersGifStream()
		{
			byte[] bytes = Convert.FromBase64String(OnePixelGif);
			using var stream = new MemoryStream(bytes);
			var converter = new Gif320Converter();

			Gif320RenderResult result = converter.RenderGif(
				stream,
				new Gif320ConversionOptions
				{
					CellsX = 2,
					CellsY = 1,
					OptimizeSize = false,
					AutoTune = false,
				}
			);

			Assert.AreEqual(2, result.CellsX);
			Assert.AreEqual(1, result.CellsY);
			Assert.IsFalse(string.IsNullOrEmpty(result.VtSequence));
		}
	}
}
