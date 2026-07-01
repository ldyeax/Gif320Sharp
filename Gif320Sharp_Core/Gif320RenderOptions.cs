using System;

namespace Gif320Sharp_Core
{
	public sealed class Gif320RenderOptions
	{
		public const int CellPixelWidth = 15;
		public const int CellPixelHeight = 12;
		public const int TerminalColumns = 80;
		public const int TerminalRows = 24;
		public const double DisplayCellAspect = 4.0 / 11.0;
		public const double DisplayPixelHeightScale =
			CellPixelWidth / (CellPixelHeight * DisplayCellAspect);

		public int CellsX { get; set; } = 16;

		public int CellsY { get; set; } = 6;

		public int MaxGlyphs { get; set; } = 94;

		public bool DoubleSize { get; set; }

		public bool AutoTune { get; set; } = true;

		public Gif320ToneSettings ToneSettings { get; set; } = new Gif320ToneSettings();

		public Gif320ResizeMode ResizeMode { get; set; } = Gif320ResizeMode.Cover;

		public Gif320GlyphReductionMode GlyphReductionMode { get; set; }
			= Gif320GlyphReductionMode.VectorQuantization;

		public int MaxReductionIterations { get; set; } = 18;

		public int AutoTuneFinalists { get; set; } = 8;

		public int AutoTuneFrequencyPreference { get; set; }

		public int AutoTuneSmoothnessPreference { get; set; }

		public int AutoTuneGlyphReusePreference { get; set; }

		public int ReverseVideoInversionTolerance { get; set; } = 4;

		public bool IncludeTerminalSetup { get; set; } = true;

		public bool IncludeTerminalReset { get; set; } = true;

		public bool CenterOnScreen { get; set; } = true;

		public int StartRow { get; set; } = 1;

		public int StartColumn { get; set; } = 1;

		public static Gif320RenderOptions FullScreenDouble()
		{
			return new Gif320RenderOptions
			{
				CellsX = 40,
				CellsY = 12,
				DoubleSize = true,
				MaxGlyphs = 94,
				ResizeMode = Gif320ResizeMode.Cover,
				AutoTune = true,
				CenterOnScreen = true,
			};
		}

		internal Gif320RenderOptions Clone()
		{
			return new Gif320RenderOptions
			{
				CellsX = CellsX,
				CellsY = CellsY,
				MaxGlyphs = MaxGlyphs,
				DoubleSize = DoubleSize,
				AutoTune = AutoTune,
				ToneSettings = ToneSettings.Clone(),
				ResizeMode = ResizeMode,
				GlyphReductionMode = GlyphReductionMode,
				MaxReductionIterations = MaxReductionIterations,
				AutoTuneFinalists = AutoTuneFinalists,
				AutoTuneFrequencyPreference = AutoTuneFrequencyPreference,
				AutoTuneSmoothnessPreference = AutoTuneSmoothnessPreference,
				AutoTuneGlyphReusePreference = AutoTuneGlyphReusePreference,
				ReverseVideoInversionTolerance = ReverseVideoInversionTolerance,
				IncludeTerminalSetup = IncludeTerminalSetup,
				IncludeTerminalReset = IncludeTerminalReset,
				CenterOnScreen = CenterOnScreen,
				StartRow = StartRow,
				StartColumn = StartColumn,
			};
		}

		internal void Validate()
		{
			if (CellsX <= 0 || CellsY <= 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(CellsX),
					"Cell dimensions must be positive."
				);
			}

			if (DoubleSize)
			{
				if (CellsX > TerminalColumns / 2 || CellsY > TerminalRows / 2)
				{
					throw new ArgumentOutOfRangeException(
						nameof(CellsX),
						"Double-size output cannot exceed 40 by 12 cells on a VT320 screen."
					);
				}
			}
			else if (CellsX > TerminalColumns || CellsY > TerminalRows)
			{
				throw new ArgumentOutOfRangeException(
					nameof(CellsX),
					"Output cannot exceed 80 by 24 cells on a VT320 screen."
				);
			}

			if (MaxGlyphs <= 0 || MaxGlyphs > 94)
			{
				throw new ArgumentOutOfRangeException(
					nameof(MaxGlyphs),
					"Use a 94-character DRCS budget from slots '!' through '~'."
				);
			}

			if (MaxReductionIterations <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(MaxReductionIterations));
			}

			if (AutoTuneFinalists <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(AutoTuneFinalists));
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

			if (ReverseVideoInversionTolerance < 0
				|| ReverseVideoInversionTolerance > CellPixelWidth * CellPixelHeight)
			{
				throw new ArgumentOutOfRangeException(nameof(ReverseVideoInversionTolerance));
			}
		}
	}
}
