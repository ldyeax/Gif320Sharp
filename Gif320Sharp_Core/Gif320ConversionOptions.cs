using System;

namespace Gif320Sharp_Core
{
	public sealed class Gif320ConversionOptions
	{
		public int RedBalance { get; set; } = 30;

		public int GreenBalance { get; set; } = 40;

		public int BlueBalance { get; set; } = 10;

		public int FullThreshold { get; set; } = 50;

		public int HalfThreshold { get; set; } = 25;

		public double Ratio { get; set; } = 0.8;

		public bool AutoTune { get; set; } = true;

		public bool DoubleSize { get; set; }

		public bool FullScreenDouble { get; set; }

		public int? CellsX { get; set; }

		public int? CellsY { get; set; }

		public int MaxGlyphs { get; set; } = 94;

		public bool AllowGlyphReduction { get; set; } = true;

		public bool OptimizeSize { get; set; } = true;

		public bool IncludeTerminalSetup { get; set; } = true;

		public bool IncludeTerminalReset { get; set; } = true;

		public bool CenterOnScreen { get; set; } = true;

		public int StartRow { get; set; } = 1;

		public int StartColumn { get; set; } = 1;

		public Gif320ResizeMode ResizeMode { get; set; } = Gif320ResizeMode.Cover;

		public Gif320DitherMode DitherMode { get; set; } = Gif320DitherMode.Checkerboard;

		public Gif320ToneSettings? ToneSettingsOverride { get; set; }

		public Gif320ConversionOptions Clone()
		{
			return new Gif320ConversionOptions
			{
				RedBalance = RedBalance,
				GreenBalance = GreenBalance,
				BlueBalance = BlueBalance,
				FullThreshold = FullThreshold,
				HalfThreshold = HalfThreshold,
				Ratio = Ratio,
				AutoTune = AutoTune,
				DoubleSize = DoubleSize,
				FullScreenDouble = FullScreenDouble,
				CellsX = CellsX,
				CellsY = CellsY,
				MaxGlyphs = MaxGlyphs,
				AllowGlyphReduction = AllowGlyphReduction,
				OptimizeSize = OptimizeSize,
				IncludeTerminalSetup = IncludeTerminalSetup,
				IncludeTerminalReset = IncludeTerminalReset,
				CenterOnScreen = CenterOnScreen,
				StartRow = StartRow,
				StartColumn = StartColumn,
				ResizeMode = ResizeMode,
				DitherMode = DitherMode,
				ToneSettingsOverride = ToneSettingsOverride?.Clone(),
			};
		}

		internal void Validate()
		{
			if (RedBalance < 0 || GreenBalance < 0 || BlueBalance < 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(RedBalance),
					"Color balance values must not be negative."
				);
			}

			if (FullThreshold < 0 || FullThreshold > 100)
			{
				throw new ArgumentOutOfRangeException(nameof(FullThreshold));
			}

			if (HalfThreshold < 0 || HalfThreshold > FullThreshold)
			{
				throw new ArgumentOutOfRangeException(nameof(HalfThreshold));
			}

			if (Ratio <= 0.0)
			{
				throw new ArgumentOutOfRangeException(nameof(Ratio));
			}

			if (MaxGlyphs <= 0 || MaxGlyphs > 94)
			{
				throw new ArgumentOutOfRangeException(nameof(MaxGlyphs));
			}

			if (StartRow <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(StartRow));
			}

			if (StartColumn <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(StartColumn));
			}
		}
	}
}
