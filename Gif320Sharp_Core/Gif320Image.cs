using System;

namespace Gif320Sharp_Core
{
	public sealed class Gif320Image
	{
		public Gif320Image(
			int width,
			int height,
			byte[] rgbPixels,
			int colorCount
		)
		{
			if (width <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(width));
			}

			if (height <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(height));
			}

			if (rgbPixels == null)
			{
				throw new ArgumentNullException(nameof(rgbPixels));
			}

			if (rgbPixels.Length != checked(width * height * 3))
			{
				throw new ArgumentException(
					"RGB pixel buffer size does not match the supplied dimensions.",
					nameof(rgbPixels)
				);
			}

			Width = width;
			Height = height;
			RgbPixels = rgbPixels;
			ColorCount = colorCount;
		}

		public int Width { get; }

		public int Height { get; }

		public byte[] RgbPixels { get; }

		public int ColorCount { get; }

		public Gif320Image Crop(int left, int top, int width, int height)
		{
			if (left < 0 || top < 0 || width <= 0 || height <= 0
				|| left + width > Width || top + height > Height)
			{
				throw new ArgumentOutOfRangeException(nameof(left));
			}

			var cropped = new byte[width * height * 3];
			for (int y = 0; y < height; y++)
			{
				Array.Copy(
					RgbPixels,
					((top + y) * Width + left) * 3,
					cropped,
					y * width * 3,
					width * 3
				);
			}

			return new Gif320Image(width, height, cropped, ColorCount);
		}
	}
}
