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

		public bool DeriveCellsYFromX { get; set; }

		public bool DeriveCellsXFromY { get; set; }

		public int MaxGlyphs { get; set; } = 94;

		public bool AllowGlyphReduction { get; set; } = true;

		public int MaxReductionIterations { get; set; } = 18;

		public int AutoTuneFrequencyPreference { get; set; }

		public int AutoTuneSmoothnessPreference { get; set; }

		public int AutoTuneGlyphReusePreference { get; set; }

		public Gif320AutoTuneLocks AutoTuneLocks { get; set; }

		public int ReverseVideoInversionTolerance { get; set; } = 4;

		public string ManualAtlas { get; set; } = string.Empty;

		public string ManualCellMap { get; set; } = string.Empty;

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
				DeriveCellsYFromX = DeriveCellsYFromX,
				DeriveCellsXFromY = DeriveCellsXFromY,
				MaxGlyphs = MaxGlyphs,
				AllowGlyphReduction = AllowGlyphReduction,
				MaxReductionIterations = MaxReductionIterations,
				AutoTuneFrequencyPreference = AutoTuneFrequencyPreference,
				AutoTuneSmoothnessPreference = AutoTuneSmoothnessPreference,
				AutoTuneGlyphReusePreference = AutoTuneGlyphReusePreference,
				AutoTuneLocks = AutoTuneLocks,
				ReverseVideoInversionTolerance = ReverseVideoInversionTolerance,
				ManualAtlas = ManualAtlas,
				ManualCellMap = ManualCellMap,
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

			if (DeriveCellsYFromX && DeriveCellsXFromY)
			{
				throw new ArgumentException("Only one automatic cell dimension can be enabled.");
			}

			if (DeriveCellsYFromX && !CellsX.HasValue)
			{
				throw new ArgumentException("Automatic cell height requires a fixed cell width.");
			}

			if (DeriveCellsXFromY && !CellsY.HasValue)
			{
				throw new ArgumentException("Automatic cell width requires a fixed cell height.");
			}

			if (MaxReductionIterations <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(MaxReductionIterations));
			}

			if (AutoTuneFrequencyPreference < -100 || AutoTuneFrequencyPreference > 100)
			{
				throw new ArgumentOutOfRangeException(nameof(AutoTuneFrequencyPreference));
			}

			if (AutoTuneSmoothnessPreference < -100 || AutoTuneSmoothnessPreference > 100)
			{
				throw new ArgumentOutOfRangeException(nameof(AutoTuneSmoothnessPreference));
			}

			if (AutoTuneGlyphReusePreference < -100 || AutoTuneGlyphReusePreference > 100)
			{
				throw new ArgumentOutOfRangeException(nameof(AutoTuneGlyphReusePreference));
			}

			if ((AutoTuneLocks & ~Gif320AutoTuneLocks.Tone) != 0)
			{
				throw new ArgumentOutOfRangeException(nameof(AutoTuneLocks));
			}

			if (ReverseVideoInversionTolerance < 0
				|| ReverseVideoInversionTolerance > Gif320RenderOptions.CellPixelWidth
					* Gif320RenderOptions.CellPixelHeight)
			{
				throw new ArgumentOutOfRangeException(nameof(ReverseVideoInversionTolerance));
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
