using System;

namespace Gif320Sharp_Core
{
	public sealed class Gif320ToneSettings
	{
		public double RedWeight { get; set; } = 0.2126;

		public double GreenWeight { get; set; } = 0.7152;

		public double BlueWeight { get; set; } = 0.0722;

		public double Brightness { get; set; }

		public double Contrast { get; set; } = 1.0;

		public double Gamma { get; set; } = 1.0;

		public double Threshold { get; set; } = 0.5;

		public double HalfThreshold { get; set; } = 0.25;

		public Gif320DitherMode DitherMode { get; set; } = Gif320DitherMode.FloydSteinberg;

		public bool UseLocalContrast { get; set; }

		public double LocalContrastClipLimit { get; set; } = 0.02;

		public Gif320ToneSettings Clone()
		{
			return new Gif320ToneSettings
			{
				RedWeight = RedWeight,
				GreenWeight = GreenWeight,
				BlueWeight = BlueWeight,
				Brightness = Brightness,
				Contrast = Contrast,
				Gamma = Gamma,
				Threshold = Threshold,
				HalfThreshold = HalfThreshold,
				DitherMode = DitherMode,
				UseLocalContrast = UseLocalContrast,
				LocalContrastClipLimit = LocalContrastClipLimit,
			};
		}

		internal void NormalizeColorWeights()
		{
			RedWeight = Math.Max(0.0, RedWeight);
			GreenWeight = Math.Max(0.0, GreenWeight);
			BlueWeight = Math.Max(0.0, BlueWeight);

			double sum = RedWeight + GreenWeight + BlueWeight;
			if (sum <= 0.0)
			{
				RedWeight = 0.2126;
				GreenWeight = 0.7152;
				BlueWeight = 0.0722;
				return;
			}

			RedWeight /= sum;
			GreenWeight /= sum;
			BlueWeight /= sum;
		}
	}
}
