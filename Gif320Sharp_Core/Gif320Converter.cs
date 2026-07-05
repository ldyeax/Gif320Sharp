using System;
using System.IO;
using System.Threading;

namespace Gif320Sharp_Core
{
	public sealed class Gif320Converter
	{
		private readonly Gif320Renderer _renderer;

		public Gif320Converter()
			: this(new Gif320Renderer())
		{
		}

		public Gif320Converter(Gif320Renderer renderer)
		{
			_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		}

		public Gif320Image LoadGif(Stream stream)
		{
			return Gif320GifDecoder.Decode(stream);
		}

		public Gif320Image LoadGifFile(string path)
		{
			return Gif320GifDecoder.DecodeFile(path);
		}

		public Gif320RenderResult RenderGif(
			Stream stream,
			Gif320ConversionOptions? options = null
		)
		{
			return Render(LoadGif(stream), options);
		}

		public Gif320RenderResult RenderGifFile(
			string path,
			Gif320ConversionOptions? options = null
		)
		{
			return Render(LoadGifFile(path), options);
		}

		public Gif320RenderResult RenderGif320PipeCompatible(
			Stream stream,
			Gif320ConversionOptions? options = null
		)
		{
			return RenderGif320PipeCompatible(LoadGif(stream), options);
		}

		public Gif320RenderResult RenderGif320PipeCompatible(
			Gif320Image image,
			Gif320ConversionOptions? options = null
		)
		{
			var legacyRenderer = new Gif320LegacyRenderer();
			return legacyRenderer.Render(image, options);
		}

		public Gif320RenderResult Render(
			Gif320Image image,
			Gif320ConversionOptions? options = null
		)
		{
			return Render(image, options, CancellationToken.None);
		}

		public Gif320RenderResult Render(
			Gif320Image image,
			Gif320ConversionOptions? options,
			CancellationToken cancellationToken
		)
		{
			if (image == null)
			{
				throw new ArgumentNullException(nameof(image));
			}

			Gif320ConversionOptions working = (options ?? new Gif320ConversionOptions()).Clone();
			working.Validate();
			Gif320RenderOptions renderOptions = CreateRenderOptions(image, working);
			return _renderer.RenderRgb(
				image.RgbPixels,
				image.Width,
				image.Height,
				renderOptions,
				cancellationToken
			);
		}

		public Gif320RenderOptions CreateRenderOptions(
			Gif320Image image,
			Gif320ConversionOptions options
		)
		{
			if (image == null)
			{
				throw new ArgumentNullException(nameof(image));
			}

			if (options == null)
			{
				throw new ArgumentNullException(nameof(options));
			}

			options.Validate();

			Gif320RenderOptions renderOptions = options.FullScreenDouble
				? Gif320RenderOptions.FullScreenDouble()
				: new Gif320RenderOptions();

			renderOptions.MaxGlyphs = options.MaxGlyphs;
			renderOptions.AutoTune = options.AutoTune;
			renderOptions.DoubleSize = options.DoubleSize || options.FullScreenDouble;
			renderOptions.ResizeMode = options.ResizeMode;
			renderOptions.IncludeTerminalSetup = options.IncludeTerminalSetup;
			renderOptions.IncludeTerminalReset = options.IncludeTerminalReset;
			renderOptions.CenterOnScreen = options.CenterOnScreen;
			renderOptions.StartRow = options.StartRow;
			renderOptions.StartColumn = options.StartColumn;
			renderOptions.MaxReductionIterations = options.MaxReductionIterations;
			renderOptions.AutoTuneFrequencyPreference = options.AutoTuneFrequencyPreference;
			renderOptions.AutoTuneSmoothnessPreference = options.AutoTuneSmoothnessPreference;
			renderOptions.AutoTuneGlyphReusePreference = options.AutoTuneGlyphReusePreference;
			renderOptions.AutoTuneLocks = options.AutoTuneLocks;
			renderOptions.ReverseVideoInversionTolerance = options.ReverseVideoInversionTolerance;
			renderOptions.ManualAtlas = options.ManualAtlas;
			renderOptions.ManualCellMap = options.ManualCellMap;
			renderOptions.GlyphReductionMode = options.AllowGlyphReduction
				? Gif320GlyphReductionMode.VectorQuantization
				: Gif320GlyphReductionMode.Exact;
			renderOptions.ToneSettings = CreateToneSettings(options);

			if (options.FullScreenDouble)
			{
				return renderOptions;
			}

			if (options.CellsX.HasValue || options.CellsY.HasValue)
			{
				(int cellsX, int cellsY) = ResolveConfiguredCellSize(image, options);
				renderOptions.CellsX = cellsX;
				renderOptions.CellsY = cellsY;
				return renderOptions;
			}

			if (options.OptimizeSize)
			{
				(int cellsX, int cellsY) = EstimateOptimizedSize(image, options);
				renderOptions.CellsX = cellsX;
				renderOptions.CellsY = cellsY;
			}

			return renderOptions;
		}

		private static (int cellsX, int cellsY) ResolveConfiguredCellSize(
			Gif320Image image,
			Gif320ConversionOptions options
		)
		{
			int maxX = options.DoubleSize || options.FullScreenDouble
				? Gif320RenderOptions.TerminalColumns / 2
				: Gif320RenderOptions.TerminalColumns;
			int maxY = options.DoubleSize || options.FullScreenDouble
				? Gif320RenderOptions.TerminalRows / 2
				: Gif320RenderOptions.TerminalRows;
			int cellsX = options.CellsX ?? 16;
			int cellsY = options.CellsY ?? 6;

			if (options.DeriveCellsYFromX)
			{
				cellsY = (int)Math.Round(
					cellsX
						* Gif320RenderOptions.DisplayCellAspect
						* image.Height
						/ Math.Max(1, image.Width)
				);
			}
			else if (options.DeriveCellsXFromY)
			{
				cellsX = (int)Math.Round(
					cellsY
						* image.Width
						/ (Math.Max(1, image.Height)
							* Gif320RenderOptions.DisplayCellAspect)
				);
			}

			return (
				Math.Clamp(cellsX, 1, maxX),
				Math.Clamp(cellsY, 1, maxY)
			);
		}

		private (int cellsX, int cellsY) EstimateOptimizedSize(
			Gif320Image image,
			Gif320ConversionOptions options
		)
		{
			int maxX = options.DoubleSize
				? Gif320RenderOptions.TerminalColumns / 2
				: Gif320RenderOptions.TerminalColumns;
			int maxY = options.DoubleSize
				? Gif320RenderOptions.TerminalRows / 2
				: Gif320RenderOptions.TerminalRows;

			double imageRatio = image.Width / (double)Math.Max(1, image.Height);
			double targetRatio = options.Ratio > 0.0 ? options.Ratio : imageRatio;
			if (targetRatio <= 0.0)
			{
				targetRatio = 0.8;
			}

			var probeOptions = new Gif320ConversionOptions
			{
				RedBalance = options.RedBalance,
				GreenBalance = options.GreenBalance,
				BlueBalance = options.BlueBalance,
				FullThreshold = options.FullThreshold,
				HalfThreshold = options.HalfThreshold,
				Ratio = options.Ratio,
				AutoTune = false,
				DoubleSize = options.DoubleSize,
				MaxGlyphs = options.MaxGlyphs,
				AllowGlyphReduction = false,
				MaxReductionIterations = options.MaxReductionIterations,
				AutoTuneFrequencyPreference = options.AutoTuneFrequencyPreference,
				AutoTuneSmoothnessPreference = options.AutoTuneSmoothnessPreference,
				AutoTuneGlyphReusePreference = options.AutoTuneGlyphReusePreference,
				AutoTuneLocks = options.AutoTuneLocks,
				ReverseVideoInversionTolerance = options.ReverseVideoInversionTolerance,
				OptimizeSize = false,
				IncludeTerminalSetup = false,
				IncludeTerminalReset = false,
				CenterOnScreen = options.CenterOnScreen,
				StartRow = options.StartRow,
				StartColumn = options.StartColumn,
				ResizeMode = options.ResizeMode,
				DitherMode = options.DitherMode,
			};

			for (int cellsY = maxY; cellsY >= 1; cellsY--)
			{
				int cellsX = Math.Max(1, (int)Math.Round(cellsY * targetRatio));
				if (cellsX > maxX)
				{
					cellsX = maxX;
				}

				if (cellsX * cellsY < options.MaxGlyphs)
				{
					return (cellsX, cellsY);
				}

				probeOptions.CellsX = cellsX;
				probeOptions.CellsY = cellsY;
				try
				{
					Gif320RenderOptions renderOptions = CreateRenderOptions(image, probeOptions);
					_renderer.RenderRgb(
						image.RgbPixels,
						image.Width,
						image.Height,
						renderOptions
					);
					return (cellsX, cellsY);
				}
				catch (InvalidOperationException)
				{
				}
			}

			return (16, 6);
		}

		private static Gif320ToneSettings CreateToneSettings(
			Gif320ConversionOptions options
		)
		{
			if (options.ToneSettingsOverride != null)
			{
				return options.ToneSettingsOverride.Clone();
			}

			return new Gif320ToneSettings
			{
				RedWeight = options.RedBalance,
				GreenWeight = options.GreenBalance,
				BlueWeight = options.BlueBalance,
				Threshold = options.FullThreshold / 100.0,
				HalfThreshold = options.HalfThreshold / 100.0,
				DitherMode = options.DitherMode,
			};
		}
	}
}
