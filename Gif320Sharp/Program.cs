using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Gif320Sharp_Core;

internal static class Program
{
	private const string Prompt = "GIF320> ";
	private const int SketchCellsX = 16;
	private const int SketchCellsY = 6;
	private const string TerminalSetup = "\u001b[63;1\"p\u001b[?3l\u001b[?5l";
	private const string MouseEnable = "\u001b[?1000h\u001b[?1002h\u001b[?1006h";
	private const string MouseDisable = "\u001b[?1006l\u001b[?1002l\u001b[?1000l";
	private const string HomeCursor = "\u001b[H";
	private const string ClearToEndOfScreen = "\u001b[J";
	private const string ClearToEndOfLine = "\u001b[K";
	private const string StandoutOn = "\u001b[1m";
	private const string StandoutOff = "\u001b[22m";
	private const string LineDrawingOn = "\u001b(0";
	private const string LineDrawingOff = "\u001b(B";
	private const int FullPreviewCellsX = Gif320RenderOptions.TerminalColumns;
	private const int FullPreviewCellsY = Gif320RenderOptions.TerminalRows;
	private const int DesiredControlsRows = 8;
	private const int BottomReservedRows = 3;
	private const int StandardInputHandle = -10;
	private const uint WaitObject0 = 0x00000000;
	private const uint WaitTimeout = 0x00000102;
	private const uint EnableLineInput = 0x0002;
	private const uint EnableEchoInput = 0x0004;
	private const uint EnableMouseInput = 0x0010;
	private const uint EnableQuickEditMode = 0x0040;
	private const uint EnableExtendedFlags = 0x0080;
	private const uint EnableVirtualTerminalInput = 0x0200;

	private enum PreviewMode
	{
		Classic,
		Full80x24,
	}

	private enum SliderKind
	{
		FullThreshold,
		HalfThreshold,
		RedBalance,
		GreenBalance,
		BlueBalance,
	}

	private sealed class SliderHitBox
	{
		public required SliderKind Kind { get; init; }

		public required int Row { get; init; }

		public required int LeftArrowColumn { get; init; }

		public required int RightArrowColumn { get; init; }

		public required int BarStartColumn { get; init; }

		public required int BarEndColumn { get; init; }

		public required int Minimum { get; init; }

		public required int Maximum { get; init; }
	}

	private sealed class MouseInputEvent
	{
		public required int Button { get; init; }

		public required int Column { get; init; }

		public required int Row { get; init; }

		public required bool Released { get; init; }
	}

	private sealed class TerminalLayout
	{
		private TerminalLayout(
			int width,
			int height,
			int previewRows,
			int previewColumns,
			int previewStartRow,
			int previewStartColumn,
			int controlsStartRow,
			int controlsEndRow,
			int messageRow,
			int promptRow
		)
		{
			Width = width;
			Height = height;
			PreviewRows = previewRows;
			PreviewColumns = previewColumns;
			PreviewStartRow = previewStartRow;
			PreviewStartColumn = previewStartColumn;
			ControlsStartRow = controlsStartRow;
			ControlsEndRow = controlsEndRow;
			MessageRow = messageRow;
			PromptRow = promptRow;
		}

		public int Width { get; }

		public int Height { get; }

		public int PreviewRows { get; }

		public int PreviewColumns { get; }

		public int PreviewStartRow { get; }

		public int PreviewStartColumn { get; }

		public int ControlsStartRow { get; }

		public int ControlsEndRow { get; }

		public int MessageRow { get; }

		public int PromptRow { get; }

		public static TerminalLayout Create(PreviewMode previewMode)
		{
			int width = Math.Max(20, GetConsoleDimension(isWidth: true, fallback: 80));
			int height = Math.Max(8, GetConsoleDimension(isWidth: false, fallback: 24));
			int desiredPreviewRows = previewMode == PreviewMode.Full80x24
				? FullPreviewCellsY
				: SketchCellsY + 2;
			int maxPreviewRows = Math.Max(1, height - BottomReservedRows - DesiredControlsRows);
			int previewRows = Math.Min(desiredPreviewRows, maxPreviewRows);
			int desiredPreviewColumns = previewMode == PreviewMode.Full80x24
				? FullPreviewCellsX
				: SketchCellsX + 2;
			int previewColumns = Math.Min(desiredPreviewColumns, width);
			int previewStartColumn = width > previewColumns
				? ((width - previewColumns) / 2) + 1
				: 1;
			int promptRow = Math.Max(1, height - 1);
			int messageRow = Math.Max(1, promptRow - 1);
			int controlsStartRow = Math.Max(previewRows + 1, messageRow - DesiredControlsRows);
			int controlsEndRow = Math.Max(
				controlsStartRow,
				Math.Min(messageRow - 1, controlsStartRow + DesiredControlsRows - 1)
			);

			return new TerminalLayout(
				width,
				height,
				previewRows,
				previewColumns,
				1,
				previewStartColumn,
				controlsStartRow,
				controlsEndRow,
				messageRow,
				promptRow
			);
		}

		public bool SameAs(TerminalLayout other)
		{
			return other != null
				&& Width == other.Width
				&& Height == other.Height
				&& PreviewRows == other.PreviewRows
				&& PreviewColumns == other.PreviewColumns
				&& PreviewStartRow == other.PreviewStartRow
				&& PreviewStartColumn == other.PreviewStartColumn
				&& ControlsStartRow == other.ControlsStartRow
				&& ControlsEndRow == other.ControlsEndRow
				&& MessageRow == other.MessageRow
				&& PromptRow == other.PromptRow;
		}
	}

	public static int Main(string[] args)
	{
		Console.OutputEncoding = Encoding.ASCII;
		try
		{
			CliOptions cli = ParseArgs(args);
			if (cli.ShowHelp)
			{
				Usage(Console.Out);
				return 0;
			}

			var converter = new Gif320Converter();
			if (cli.PipeMode)
			{
				using Stream input = Console.OpenStandardInput();
				Gif320RenderResult result = UseOriginalPipeRenderer(cli.Conversion)
					? converter.RenderGif320PipeCompatible(input, cli.Conversion)
					: converter.RenderGif(input, cli.Conversion);
				WriteSequence(Console.OpenStandardOutput(), result.VtSequence);
				return 0;
			}

			if (cli.Files.Count == 0)
			{
				Usage(Console.Error);
				return 1;
			}

			foreach (string file in cli.Files)
			{
				if (!string.IsNullOrEmpty(cli.OutputPath))
				{
					WriteFile(converter, file, cli.OutputPath, cli.Conversion);
				}
				else
				{
					RunInteractive(
						converter,
						file,
						cli.Conversion,
						cli.InteractiveCompatibilityMode
					);
				}
			}

			return 0;
		}
		catch (Exception ex) when (ex is Gif320Exception
			or IOException
			or UnauthorizedAccessException
			or ArgumentException
			or InvalidOperationException)
		{
			Console.Error.WriteLine("gif320: " + ex.Message);
			return 1;
		}
	}

	private static void RunInteractive(
		Gif320Converter converter,
		string file,
		Gif320ConversionOptions baseOptions,
		bool compatibilityMode
	)
	{
		Gif320Image image = converter.LoadGifFile(file);
		Gif320ConversionOptions options = baseOptions.Clone();
		bool immediateRendering = compatibilityMode || IsModernTerminal();
		bool runStartupAdvancedTune = options.AutoTune
			&& !compatibilityMode
			&& immediateRendering;
		options.AutoTune = false;
		options.IncludeTerminalSetup = false;
		options.IncludeTerminalReset = true;

		int left = 0;
		int top = 0;
		int right = image.Width;
		int bottom = image.Height;
		Gif320RenderResult? last = null;
		PreviewMode previewMode = PreviewMode.Classic;
		TerminalLayout layout = TerminalLayout.Create(previewMode);
		var sliderHitBoxes = new List<SliderHitBox>();
		SliderKind? activeSlider = null;
		bool mouseInputEnabled = !Console.IsInputRedirected && immediateRendering;
		bool useStreamInput = false;
		uint? originalInputMode = mouseInputEnabled
			? TryEnableInteractiveInput(out useStreamInput)
			: null;
		bool vtMouseEnabled = mouseInputEnabled;
		bool originalTreatControlCAsInput = false;
		if (useStreamInput)
		{
			originalTreatControlCAsInput = Console.TreatControlCAsInput;
			Console.TreatControlCAsInput = true;
		}

		try
		{
			Console.Write(TerminalSetup);
			if (mouseInputEnabled)
			{
				Console.Write(MouseDisable);
			}

			if (vtMouseEnabled)
			{
				Console.Write(MouseEnable);
			}

			if (runStartupAdvancedTune)
			{
				ApplyAdvancedInteractiveTune(
					converter,
					image,
					options,
					left,
					top,
					right,
					bottom,
					previewMode,
					layout
				);
			}

			last = DrawInteractiveScreen(
				converter,
				image,
				options,
				file,
				left,
				top,
				right,
				bottom,
				previewMode,
				layout,
				sliderHitBoxes
			);

			while (true)
			{
				TerminalLayout currentLayout = TerminalLayout.Create(previewMode);
				if (!currentLayout.SameAs(layout))
				{
					layout = currentLayout;
					last = DrawInteractiveScreen(
						converter,
						image,
						options,
						file,
						left,
						top,
						right,
						bottom,
						previewMode,
						layout,
						sliderHitBoxes
					);
				}

				bool stateChangedByInput;
				string? line = ReadInteractiveCommand(
					previewMode,
					ref layout,
					() =>
					{
						last = DrawInteractiveScreen(
							converter,
							image,
							options,
							file,
							left,
							top,
							right,
							bottom,
							previewMode,
							layout,
							sliderHitBoxes
						);
					},
					sliderHitBoxes,
					options,
					ref activeSlider,
					useStreamInput,
					out stateChangedByInput
				);
			if (line == null)
			{
				return;
			}

			if (stateChangedByInput)
			{
				layout = TerminalLayout.Create(previewMode);
				last = DrawInteractiveScreen(
					converter,
					image,
					options,
					file,
					left,
					top,
					right,
					bottom,
					previewMode,
					layout,
					sliderHitBoxes
				);
				continue;
			}

			ClearMessages(layout);
			string[] parts = line.Split(
				new[] { ' ', '\t' },
				StringSplitOptions.RemoveEmptyEntries
			);
			string command = parts.Length == 0 ? string.Empty : parts[0].ToLowerInvariant();
			bool redrawSketch = false;
			switch (command.Length == 0 ? '\0' : command[0])
			{
				case '\0':
					redrawSketch = true;
					break;
				case 'q':
					MoveCursor(layout.MessageRow, 1);
					Console.Write(ClearToEndOfScreen);
					return;
				case '?':
					WriteProgramInfo(layout);
					break;
				case 't':
					if (command == "tune")
					{
						redrawSketch = ApplyAdvancedInteractiveTune(
							converter,
							image,
							options,
							left,
							top,
							right,
							bottom,
							previewMode,
							layout,
							parts
						);
					}
					else
					{
						redrawSketch = SetThresholds(parts, options);
					}

					break;
				case 'b':
					redrawSketch = SetBalance(parts, options);
					break;
				case 'a':
					redrawSketch = ApplyAdvancedInteractiveTune(
						converter,
						image,
						options,
						left,
						top,
						right,
						bottom,
						previewMode,
						layout,
						parts
					);
					break;
				case 'r':
					redrawSketch = SetRatio(parts, options);
					break;
				case 'm':
					redrawSketch = SetPreviewMode(parts, ref previewMode);
					break;
				case 'o':
					bool promptForOptimizedSave = command != "optimize-preview";
					last = RenderOptimized(
						converter,
						image,
						options,
						left,
						top,
						right,
						bottom,
						parts
					);
					Console.Write(HomeCursor);
					Console.Write(ClearToEndOfScreen);
					Console.Out.Write(last.VtSequence);
					int optimizedGlyphCount = last.GlyphCount;
					int optimizedCellsX = last.CellsX;
					int optimizedCellsY = last.CellsY;
					if (promptForOptimizedSave)
					{
						MoveCursor(layout.MessageRow, 1);
						Console.Write($"cells used: {last.GlyphCount}/{options.MaxGlyphs} -- size: {last.CellsX}x{last.CellsY} -- save as? ");
						string? saveAs = Console.ReadLine();
						if (!string.IsNullOrWhiteSpace(saveAs))
						{
							File.WriteAllText(saveAs, last.VtSequence, Encoding.ASCII);
						}
					}
					last = DrawInteractiveScreen(
						converter,
						image,
						options,
						file,
						left,
						top,
						right,
						bottom,
						previewMode,
						layout,
						sliderHitBoxes
					);
					if (!promptForOptimizedSave)
					{
						WriteStatus(
							layout,
							$"Optimized preview: {optimizedGlyphCount}/{options.MaxGlyphs} cells, {optimizedCellsX}x{optimizedCellsY}."
						);
					}
					break;
				case 's':
					SaveLast(parts, last);
					break;
				case 'd':
					SaveDouble(
						parts,
						converter,
						image,
						options,
						left,
						top,
						right,
						bottom
					);
					break;
				case 'z':
					redrawSketch = Zoom(image, ref left, ref top, ref right, ref bottom, parts, zoomIn: true);
					break;
				case 'x':
					redrawSketch = Zoom(image, ref left, ref top, ref right, ref bottom, parts, zoomIn: false);
					break;
				case 'h':
					redrawSketch = Pan(image, ref left, ref right, parts, horizontal: true, negative: true);
					break;
				case 'l':
					redrawSketch = Pan(image, ref left, ref right, parts, horizontal: true, negative: false);
					break;
				case 'k':
					redrawSketch = Pan(image, ref top, ref bottom, parts, horizontal: false, negative: true);
					break;
				case 'j':
					redrawSketch = Pan(image, ref top, ref bottom, parts, horizontal: false, negative: false);
					break;
				case 'f':
					left = 0;
					top = 0;
					right = image.Width;
					bottom = image.Height;
					redrawSketch = true;
					break;
				default:
					Console.WriteLine("Unknown command. Use ? for help.");
					break;
			}

			if (redrawSketch)
			{
				if (immediateRendering
					|| command.Length == 0
					|| command == "tune"
					|| command[0] == 'a')
				{
					layout = TerminalLayout.Create(previewMode);
					last = DrawInteractiveScreen(
						converter,
						image,
						options,
						file,
						left,
						top,
						right,
						bottom,
						previewMode,
						layout,
						sliderHitBoxes
					);
				}
				else
				{
					WriteStatus(layout, "Changed. Press Enter to redraw on this terminal.");
				}
			}
			}
		}
		finally
		{
			if (mouseInputEnabled)
			{
				Console.Write(MouseDisable);
			}

			RestoreInteractiveInput(originalInputMode);
			if (useStreamInput)
			{
				Console.TreatControlCAsInput = originalTreatControlCAsInput;
			}
		}
	}

	private static Gif320RenderResult DrawInteractiveScreen(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		string file,
		int left,
		int top,
		int right,
		int bottom,
		PreviewMode previewMode,
		TerminalLayout layout,
		List<SliderHitBox> sliderHitBoxes
	)
	{
		Gif320RenderResult result = RenderPreview(
			converter,
			image,
			options,
			left,
			top,
			right,
			bottom,
			previewMode
		);

		Console.Write(HomeCursor);
		Console.Write(ClearToEndOfScreen);
		if (previewMode == PreviewMode.Classic)
		{
			DrawClassicPreviewFrame(layout);
		}

		WritePreview(result, previewMode, layout);
		DrawControls(
			image,
			options,
			result,
			file,
			left,
			top,
			right,
			bottom,
			previewMode,
			layout,
			sliderHitBoxes
		);
		ClearMessages(layout);

		return result;
	}

	private static Gif320RenderResult RenderPreview(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom,
		PreviewMode previewMode
	)
	{
		return previewMode == PreviewMode.Full80x24
			? RenderFullScreenPreview(converter, image, options, left, top, right, bottom)
			: RenderClassicSketch(converter, image, options, left, top, right, bottom);
	}

	private static Gif320RenderResult RenderClassicSketch(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom
	)
	{
		Gif320ConversionOptions sketch = options.Clone();
		sketch.FullScreenDouble = false;
		sketch.DoubleSize = false;
		sketch.CellsX = SketchCellsX;
		sketch.CellsY = SketchCellsY;
		sketch.OptimizeSize = false;
		sketch.ResizeMode = Gif320ResizeMode.Stretch;
		sketch.IncludeTerminalSetup = false;
		sketch.IncludeTerminalReset = true;
		sketch.CenterOnScreen = false;
		sketch.StartRow = 1;
		sketch.StartColumn = 1;
		return converter.Render(Crop(image, left, top, right, bottom), sketch);
	}

	private static Gif320RenderResult RenderFullScreenPreview(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom
	)
	{
		Gif320ConversionOptions fullScreen = options.Clone();
		fullScreen.FullScreenDouble = false;
		fullScreen.DoubleSize = false;
		fullScreen.CellsX = FullPreviewCellsX;
		fullScreen.CellsY = FullPreviewCellsY;
		fullScreen.OptimizeSize = false;
		fullScreen.ResizeMode = Gif320ResizeMode.Stretch;
		fullScreen.IncludeTerminalSetup = false;
		fullScreen.IncludeTerminalReset = true;
		fullScreen.CenterOnScreen = false;
		fullScreen.StartRow = 1;
		fullScreen.StartColumn = 1;
		return converter.Render(Crop(image, left, top, right, bottom), fullScreen);
	}

	private static bool ApplyAdvancedInteractiveTune(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom,
		PreviewMode previewMode,
		TerminalLayout layout,
		string[]? parts = null
	)
	{
		PreviewMode tuningMode = previewMode;
		if (parts != null && parts.Length >= 2)
		{
			if (parts[1].Equals("current", StringComparison.OrdinalIgnoreCase))
			{
				tuningMode = previewMode;
			}
			else if (!TryParsePreviewMode(parts[1], out tuningMode))
			{
				WriteStatus(layout, "Advanced tune target must be old/classic/sketch, 80x24/full, or current.");
				return false;
			}
		}

		WriteStatus(
			layout,
			$"Running advanced tune for {FormatPreviewMode(tuningMode)} preview..."
		);

		Gif320ConversionOptions tuning = options.Clone();
		tuning.AutoTune = true;
		tuning.ToneSettingsOverride = null;
		tuning.AllowGlyphReduction = true;
		Gif320RenderResult tuned = RenderPreview(
			converter,
			image,
			tuning,
			left,
			top,
			right,
			bottom,
			tuningMode
		);

		options.ToneSettingsOverride = tuned.ToneSettings.Clone();
		options.AutoTune = false;
		WriteStatus(
			layout,
			$"Advanced tune applied: score {tuned.Score:0.000}, {tuned.GlyphCount}/{options.MaxGlyphs} glyphs."
		);
		return true;
	}

	private static bool TryParsePreviewMode(string value, out PreviewMode previewMode)
	{
		value = value.ToLowerInvariant();
		if (value is "old" or "classic" or "sketch")
		{
			previewMode = PreviewMode.Classic;
			return true;
		}

		if (value is "80" or "80x24" or "full" or "fullscreen")
		{
			previewMode = PreviewMode.Full80x24;
			return true;
		}

		previewMode = PreviewMode.Classic;
		return false;
	}

	private static string FormatPreviewMode(PreviewMode previewMode)
	{
		return previewMode == PreviewMode.Full80x24 ? "80x24" : "old";
	}

	private static Gif320RenderResult RenderOptimized(
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom,
		string[] parts
	)
	{
		Gif320ConversionOptions optimized = options.Clone();
		optimized.OptimizeSize = true;
		if (parts.Length >= 3
			&& int.TryParse(parts[1], out int cellsX)
			&& int.TryParse(parts[2], out int cellsY))
		{
			optimized.CellsX = cellsX;
			optimized.CellsY = cellsY;
			optimized.OptimizeSize = false;
		}

		optimized.IncludeTerminalSetup = true;
		optimized.IncludeTerminalReset = true;
		return converter.Render(Crop(image, left, top, right, bottom), optimized);
	}

	private static void SaveLast(string[] parts, Gif320RenderResult? last)
	{
		if (parts.Length != 2)
		{
			Console.WriteLine("You must provide a file name to output.");
			return;
		}

		if (last == null)
		{
			Console.WriteLine("No developed image is available yet.");
			return;
		}

		File.WriteAllText(parts[1], last.VtSequence, Encoding.ASCII);
		Console.WriteLine($"Output saved in \"{parts[1]}\".");
	}

	private static void SaveDouble(
		string[] parts,
		Gif320Converter converter,
		Gif320Image image,
		Gif320ConversionOptions options,
		int left,
		int top,
		int right,
		int bottom
	)
	{
		if (parts.Length != 2)
		{
			Console.WriteLine("You must provide a file name to output.");
			return;
		}

		Gif320ConversionOptions doubled = options.Clone();
		doubled.DoubleSize = true;
		Gif320RenderResult result = converter.Render(
			Crop(image, left, top, right, bottom),
			doubled
		);
		File.WriteAllText(parts[1], result.VtSequence, Encoding.ASCII);
		Console.WriteLine($"Output saved in \"{parts[1]}\".");
	}

	private static Gif320Image Crop(
		Gif320Image image,
		int left,
		int top,
		int right,
		int bottom
	)
	{
		return image.Crop(left, top, right - left, bottom - top);
	}

	private static bool SetThresholds(
		string[] parts,
		Gif320ConversionOptions options
	)
	{
		if (parts.Length != 3
			|| !int.TryParse(parts[1], out int full)
			|| !int.TryParse(parts[2], out int half)
			|| half < 0
			|| full < half
			|| full > 100)
		{
			Console.WriteLine("The thresholds must be in the range 0 <= half <= full <= 100.");
			return false;
		}

		options.FullThreshold = full;
		options.HalfThreshold = half;
		options.AutoTune = false;
		options.ToneSettingsOverride = null;
		return true;
	}

	private static bool SetBalance(
		string[] parts,
		Gif320ConversionOptions options
	)
	{
		if (parts.Length != 4
			|| !int.TryParse(parts[1], out int red)
			|| !int.TryParse(parts[2], out int green)
			|| !int.TryParse(parts[3], out int blue)
			|| red < 0
			|| green < 0
			|| blue < 0)
		{
			Console.WriteLine("You must provide 3 non-negative integer RGB values.");
			return false;
		}

		options.RedBalance = red;
		options.GreenBalance = green;
		options.BlueBalance = blue;
		options.AutoTune = false;
		options.ToneSettingsOverride = null;
		return true;
	}

	private static bool SetRatio(
		string[] parts,
		Gif320ConversionOptions options
	)
	{
		if (parts.Length != 2
			|| !double.TryParse(parts[1], out double ratio)
			|| ratio <= 0.0)
		{
			Console.WriteLine("The ratio must be a positive number.");
			return false;
		}

		options.Ratio = ratio;
		return true;
	}

	private static bool SetPreviewMode(string[] parts, ref PreviewMode previewMode)
	{
		if (parts.Length == 1)
		{
			previewMode = previewMode == PreviewMode.Classic
				? PreviewMode.Full80x24
				: PreviewMode.Classic;
			return true;
		}

		string value = parts[1].ToLowerInvariant();
		if (value is "old" or "classic" or "sketch")
		{
			previewMode = PreviewMode.Classic;
			return true;
		}

		if (value is "80" or "80x24" or "full" or "fullscreen")
		{
			previewMode = PreviewMode.Full80x24;
			return true;
		}

		Console.WriteLine("Mode must be old/classic/sketch or 80x24/full.");
		return false;
	}

	private static bool Zoom(
		Gif320Image image,
		ref int left,
		ref int top,
		ref int right,
		ref int bottom,
		string[] parts,
		bool zoomIn
	)
	{
		int precision = ParsePrecision(parts);
		int xShift = Math.Max(1, image.Width * precision / 200);
		int yShift = Math.Max(1, image.Height * precision / 200);
		if (zoomIn)
		{
			if (right - left <= xShift * 2 || bottom - top <= yShift * 2)
			{
				Console.WriteLine("Can't zoom in any further.");
				return false;
			}

			left += xShift;
			right -= xShift;
			top += yShift;
			bottom -= yShift;
		}
		else
		{
			left = Math.Max(0, left - xShift);
			right = Math.Min(image.Width, right + xShift);
			top = Math.Max(0, top - yShift);
			bottom = Math.Min(image.Height, bottom + yShift);
		}

		return true;
	}

	private static bool Pan(
		Gif320Image image,
		ref int start,
		ref int end,
		string[] parts,
		bool horizontal,
		bool negative
	)
	{
		int precision = ParsePrecision(parts);
		int extent = horizontal ? image.Width : image.Height;
		int shift = Math.Max(1, extent * precision / 200);
		if (negative)
		{
			shift = -shift;
		}

		int width = end - start;
		int nextStart = start + shift;
		int nextEnd = end + shift;
		if (nextStart < 0)
		{
			nextStart = 0;
			nextEnd = width;
		}
		else if (nextEnd > extent)
		{
			nextEnd = extent;
			nextStart = extent - width;
		}

		if (nextStart == start && nextEnd == end)
		{
			Console.WriteLine("Can't pan any further.");
			return false;
		}

		start = nextStart;
		end = nextEnd;
		return true;
	}

	private static int ParsePrecision(string[] parts)
	{
		return parts.Length >= 2 && int.TryParse(parts[1], out int precision)
			? Math.Max(1, precision)
			: 10;
	}

	private static void WritePreview(
		Gif320RenderResult result,
		PreviewMode previewMode,
		TerminalLayout layout
	)
	{
		int firstRow = 0;
		int firstColumn = 0;
		int rows = Math.Min(layout.PreviewRows, result.ScreenRows.Length);
		int columns = previewMode == PreviewMode.Full80x24
			? Math.Min(layout.PreviewColumns, FullPreviewCellsX)
			: Math.Min(SketchCellsX, Math.Max(1, layout.PreviewColumns - 2));
		int row = previewMode == PreviewMode.Full80x24
			? layout.PreviewStartRow
			: layout.PreviewStartRow + 1;
		int column = previewMode == PreviewMode.Full80x24
			? layout.PreviewStartColumn
			: layout.PreviewStartColumn + 1;

		if (previewMode == PreviewMode.Classic)
		{
			rows = Math.Min(SketchCellsY, Math.Max(1, layout.PreviewRows - 2));
		}

		Console.Write(BuildPreviewSequence(
			result,
			row,
			column,
			firstRow,
			rows,
			firstColumn,
			columns
		));
	}

	private static string BuildPreviewSequence(
		Gif320RenderResult result,
		int startRow,
		int startColumn,
		int firstRow,
		int rows,
		int firstColumn,
		int columns
	)
	{
		var builder = new StringBuilder();
		builder.Append("\u001bP1;1;0;15;1;2;12;0{ @");
		for (int i = 0; i < result.GlyphSixelPatterns.Count; i++)
		{
			if (i > 0)
			{
				builder.Append(';');
			}

			builder.Append(result.GlyphSixelPatterns[i]);
		}

		builder.Append("\u001b\\");
		builder.Append("\u001b) @");
		builder.Append('\u000e');
		builder.Append(StandoutOn);

		for (int i = 0; i < rows; i++)
		{
			int sourceRow = firstRow + i;
			if (sourceRow < 0 || sourceRow >= result.ScreenRows.Length)
			{
				continue;
			}

			string row = result.ScreenRows[sourceRow];
			if (firstColumn >= row.Length)
			{
				continue;
			}

			int length = Math.Min(columns, row.Length - firstColumn);
			AppendCursorMove(builder, startRow + i, startColumn);
			builder.Append("\u001b#5");
			builder.Append(row.Substring(firstColumn, length));
		}

		builder.Append('\u000f');
		builder.Append(StandoutOff);
		builder.Append("\u001b(B");
		builder.Append("\u001b)B");
		return builder.ToString();
	}

	private static void DrawClassicPreviewFrame(TerminalLayout layout)
	{
		if (layout.PreviewRows < 2 || layout.PreviewColumns < 2)
		{
			return;
		}

		int bottom = layout.PreviewStartRow + layout.PreviewRows - 1;
		int right = layout.PreviewStartColumn + layout.PreviewColumns - 1;
		DrawBox(layout.PreviewStartRow, layout.PreviewStartColumn, bottom, right);
	}

	private static void DrawControls(
		Gif320Image image,
		Gif320ConversionOptions options,
		Gif320RenderResult result,
		string file,
		int left,
		int top,
		int right,
		int bottom,
		PreviewMode previewMode,
		TerminalLayout layout,
		List<SliderHitBox> sliderHitBoxes
	)
	{
		sliderHitBoxes.Clear();
		int row = layout.ControlsStartRow;
		string mode = previewMode == PreviewMode.Full80x24 ? "80x24" : "old";
		string toneMode = options.ToneSettingsOverride == null ? "manual" : "advanced";
		WriteLeftRight(
			row++,
			$"Mode: {mode}  Tone: {toneMode}",
			$"Cells: {result.GlyphCount}/{options.MaxGlyphs}  Size: {result.CellsX}x{result.CellsY}",
			layout.Width
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteLeftRight(
			row++,
			$"Zoom: ({left},{top})/({right},{bottom})",
			$"Image: {image.Width} x {image.Height} x {image.ColorCount}  File: {Path.GetFileName(file)}",
			layout.Width
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteSlider(
			row++,
			"Threshold F",
			options.FullThreshold,
			0,
			100,
			SliderKind.FullThreshold,
			layout,
			sliderHitBoxes
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteSlider(
			row++,
			"Threshold H",
			options.HalfThreshold,
			0,
			100,
			SliderKind.HalfThreshold,
			layout,
			sliderHitBoxes
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteSlider(
			row++,
			"Balance R",
			options.RedBalance,
			0,
			Math.Max(100, options.RedBalance),
			SliderKind.RedBalance,
			layout,
			sliderHitBoxes
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteSlider(
			row++,
			"Balance G",
			options.GreenBalance,
			0,
			Math.Max(100, options.GreenBalance),
			SliderKind.GreenBalance,
			layout,
			sliderHitBoxes
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteSlider(
			row++,
			"Balance B",
			options.BlueBalance,
			0,
			Math.Max(100, options.BlueBalance),
			SliderKind.BlueBalance,
			layout,
			sliderHitBoxes
		);
		if (row > layout.ControlsEndRow)
		{
			return;
		}

		WriteLeftRight(
			row,
			"Hotkeys: z/x zoom  h/j/k/l pan  f reset  a tune  o optimize",
			"m mode  ? help  q quit  Esc command",
			layout.Width
		);
	}

	private static void WriteSlider(
		int row,
		string label,
		int value,
		int minimum,
		int maximum,
		SliderKind kind,
		TerminalLayout layout,
		List<SliderHitBox> sliderHitBoxes
	)
	{
		int width = layout.Width;
		int labelWidth = Math.Min(12, Math.Max(4, width / 4));
		int valueWidth = Math.Max(3, maximum.ToString(CultureInfo.InvariantCulture).Length);
		int fixedWidth = labelWidth + 8 + valueWidth;
		int barWidth = Math.Min(32, Math.Max(3, width - fixedWidth));
		string labelText = Clip(label, labelWidth).PadRight(labelWidth);
		int safeMaximum = Math.Max(minimum + 1, maximum);
		int clamped = Math.Clamp(value, minimum, safeMaximum);
		int markerIndex = (int)Math.Round(
			(double)(clamped - minimum) * (barWidth - 1) / (safeMaximum - minimum)
		);
		char[] bar = new char[barWidth];
		for (int i = 0; i < bar.Length; i++)
		{
			bar[i] = i < markerIndex ? '=' : '-';
		}

		bar[markerIndex] = '|';
		string valueText = clamped.ToString(CultureInfo.InvariantCulture).PadLeft(valueWidth);
		string text = $"{labelText} < [{new string(bar)}] > {valueText}";
		WriteAt(row, 1, Clip(text, width));

		int leftArrowColumn = labelWidth + 2;
		int barStartColumn = labelWidth + 5;
		int barEndColumn = barStartColumn + barWidth - 1;
		int rightArrowColumn = barEndColumn + 3;
		if (rightArrowColumn <= width)
		{
			sliderHitBoxes.Add(new SliderHitBox
			{
				Kind = kind,
				Row = row,
				LeftArrowColumn = leftArrowColumn,
				RightArrowColumn = rightArrowColumn,
				BarStartColumn = barStartColumn,
				BarEndColumn = barEndColumn,
				Minimum = minimum,
				Maximum = safeMaximum,
			});
		}
	}

	private static void WriteLeftRight(int row, string left, string right, int width)
	{
		left = Clip(left, width);
		right = Clip(right, width);
		int rightColumn = Math.Max(1, width - right.Length + 1);
		int leftLimit = Math.Max(0, rightColumn - 2);
		WriteAt(row, 1, Clip(left, leftLimit));
		if (right.Length > 0)
		{
			WriteAt(row, rightColumn, right);
		}
	}

	private static void WriteProgramInfo(TerminalLayout layout)
	{
		WriteAt(layout.MessageRow, 1, Clip(
			"Drag bars/click arrows for tone; hotkeys run immediately; Esc opens command input.",
			layout.Width
		));
	}

	private static void WriteStatus(TerminalLayout layout, string message)
	{
		MoveCursor(layout.MessageRow, 1);
		Console.Write(ClearToEndOfLine);
		Console.Write(Clip(message, layout.Width));
	}

	private static void DrawBox(int top, int left, int bottom, int right)
	{
		Console.Write(LineDrawingOn);
		WriteAt(top, left, "l" + new string('q', right - left - 1) + "k");
		for (int row = top + 1; row < bottom; row++)
		{
			WriteAt(row, left, "x");
			WriteAt(row, right, "x");
		}

		WriteAt(bottom, left, "m" + new string('q', right - left - 1) + "j");
		Console.Write(LineDrawingOff);
	}

	private static void ClearMessages(TerminalLayout layout)
	{
		MoveCursor(layout.MessageRow, 1);
		Console.Write(ClearToEndOfScreen);
	}

	private static string? ReadInteractiveCommand(
		PreviewMode previewMode,
		ref TerminalLayout layout,
		Action redraw,
		List<SliderHitBox> sliderHitBoxes,
		Gif320ConversionOptions options,
		ref SliderKind? activeSlider,
		bool useStreamInput,
		out bool stateChanged
	)
	{
		stateChanged = false;
		if (Console.IsInputRedirected)
		{
			WritePrompt(previewMode, layout);
			return Console.ReadLine();
		}

		if (useStreamInput && TryGetConsoleInputHandle(out IntPtr inputHandle))
		{
			return ReadInteractiveCommandStream(
				inputHandle,
				previewMode,
				ref layout,
				redraw,
				sliderHitBoxes,
				options,
				ref activeSlider,
				out stateChanged
			);
		}

		WriteHotkeyPrompt(previewMode, layout);
		bool discardingMouseSequence = false;
		while (true)
		{
			TerminalLayout currentLayout = TerminalLayout.Create(previewMode);
			if (!currentLayout.SameAs(layout))
			{
				layout = currentLayout;
				redraw();
				WriteHotkeyPrompt(previewMode, layout);
			}

			if (!Console.KeyAvailable)
			{
				System.Threading.Thread.Sleep(20);
				continue;
			}

			ConsoleKeyInfo key = Console.ReadKey(intercept: true);
			if (discardingMouseSequence)
			{
				if (key.KeyChar == 'M' || key.KeyChar == 'm')
				{
					discardingMouseSequence = false;
				}

				continue;
			}

			if (key.Key == ConsoleKey.C
				&& (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
			{
				return null;
			}

			if (key.Key == ConsoleKey.Escape)
			{
				string escapeSequence = ReadPendingEscapeSequence();
				if (TryParseMouseEvent(escapeSequence, out MouseInputEvent? mouseEvent)
					&& mouseEvent != null)
				{
					if (HandleMouseEvent(
						mouseEvent,
						sliderHitBoxes,
						options,
						ref activeSlider
					))
					{
						stateChanged = true;
						return string.Empty;
					}

					continue;
				}

				if (IsMouseSequencePrefix(escapeSequence))
				{
					discardingMouseSequence = true;
					continue;
				}

				if (escapeSequence.Length > 1)
				{
					continue;
				}

				WritePrompt(previewMode, layout);
				return ReadCommandLineWithResize(previewMode, ref layout, redraw);
			}

			if (TryMapHotkey(key, layout, out string? command))
			{
				return command;
			}
		}
	}

	private static string? ReadInteractiveCommandStream(
		IntPtr inputHandle,
		PreviewMode previewMode,
		ref TerminalLayout layout,
		Action redraw,
		List<SliderHitBox> sliderHitBoxes,
		Gif320ConversionOptions options,
		ref SliderKind? activeSlider,
		out bool stateChanged
	)
	{
		stateChanged = false;
		Stream input = Console.OpenStandardInput();
		WriteHotkeyPrompt(previewMode, layout);
		while (true)
		{
			int value = ReadInputByteBlocking(
				input,
				inputHandle,
				previewMode,
				ref layout,
				redraw,
				commandPrompt: false,
				commandBuffer: null
			);
			if (value < 0)
			{
				return null;
			}

			char keyChar = (char)value;
			if (keyChar == '\u0003')
			{
				return null;
			}

			if (keyChar == '\r' || keyChar == '\n')
			{
				return string.Empty;
			}

			if (keyChar == '\u001b')
			{
				string escapeSequence = ReadStreamEscapeSequence(input, inputHandle);
				if (TryParseMouseEvent(escapeSequence, out MouseInputEvent? mouseEvent)
					&& mouseEvent != null)
				{
					if (HandleMouseEvent(
						mouseEvent,
						sliderHitBoxes,
						options,
						ref activeSlider
					))
					{
						stateChanged = true;
						return string.Empty;
					}

					continue;
				}

				if (escapeSequence.Length > 1)
				{
					continue;
				}

				WritePrompt(previewMode, layout);
				return ReadCommandLineStreamWithResize(
					input,
					inputHandle,
					previewMode,
					ref layout,
					redraw
				);
			}

			if (TryMapHotkeyChar(
				keyChar,
				hasControl: false,
				hasAlt: false,
				layout,
				out string? command
			))
			{
				return command;
			}
		}
	}

	private static string? ReadCommandLineStreamWithResize(
		Stream input,
		IntPtr inputHandle,
		PreviewMode previewMode,
		ref TerminalLayout layout,
		Action redraw
	)
	{
		var buffer = new StringBuilder();
		while (true)
		{
			int value = ReadInputByteBlocking(
				input,
				inputHandle,
				previewMode,
				ref layout,
				redraw,
				commandPrompt: true,
				commandBuffer: buffer
			);
			if (value < 0)
			{
				return null;
			}

			char keyChar = (char)value;
			if (keyChar == '\u0003')
			{
				return null;
			}

			if (keyChar == '\r' || keyChar == '\n')
			{
				Console.WriteLine();
				return buffer.ToString();
			}

			if (keyChar == '\b' || keyChar == '\u007f')
			{
				if (buffer.Length > 0)
				{
					buffer.Length--;
					Console.Write("\b \b");
				}

				continue;
			}

			if (!char.IsControl(keyChar))
			{
				buffer.Append(keyChar);
				Console.Write(keyChar);
			}
		}
	}

	private static int ReadInputByteBlocking(
		Stream input,
		IntPtr inputHandle,
		PreviewMode previewMode,
		ref TerminalLayout layout,
		Action redraw,
		bool commandPrompt,
		StringBuilder? commandBuffer
	)
	{
		while (true)
		{
			TerminalLayout currentLayout = TerminalLayout.Create(previewMode);
			if (!currentLayout.SameAs(layout))
			{
				layout = currentLayout;
				redraw();
				if (commandPrompt)
				{
					WritePrompt(previewMode, layout);
					if (commandBuffer != null)
					{
						Console.Write(commandBuffer.ToString());
					}
				}
				else
				{
					WriteHotkeyPrompt(previewMode, layout);
				}
			}

			if (WaitForInput(inputHandle, timeoutMilliseconds: 10))
			{
				return input.ReadByte();
			}
		}
	}

	private static string ReadStreamEscapeSequence(Stream input, IntPtr inputHandle)
	{
		var builder = new StringBuilder();
		builder.Append('\u001b');
		if (!TryReadInputByteWithin(
			input,
			inputHandle,
			timeoutMilliseconds: 120,
			out int value
		))
		{
			return builder.ToString();
		}

		builder.Append((char)value);
		if (builder.Length == 2 && builder[1] != '[')
		{
			return builder.ToString();
		}

		while (builder.Length < 64)
		{
			if (IsCompleteMouseSequence(builder) || IsCompleteControlSequence(builder))
			{
				break;
			}

			bool mouseSequence = IsMouseSequencePrefix(builder.ToString());
			if (!TryReadInputByteWithin(
				input,
				inputHandle,
				timeoutMilliseconds: mouseSequence ? 1000 : 120,
				out value
			))
			{
				break;
			}

			builder.Append((char)value);
		}

		return builder.ToString();
	}

	private static bool TryReadInputByteWithin(
		Stream input,
		IntPtr inputHandle,
		int timeoutMilliseconds,
		out int value
	)
	{
		if (WaitForInput(inputHandle, timeoutMilliseconds))
		{
			value = input.ReadByte();
			return value >= 0;
		}

		value = -1;
		return false;
	}

	private static bool TryMapHotkey(
		ConsoleKeyInfo key,
		TerminalLayout layout,
		out string? command
	)
	{
		command = null;
		if ((key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control
			|| (key.Modifiers & ConsoleModifiers.Alt) == ConsoleModifiers.Alt)
		{
			return false;
		}

		if (key.Key == ConsoleKey.Enter)
		{
			command = string.Empty;
			return true;
		}

		return TryMapHotkeyChar(
			key.KeyChar,
			(key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control,
			(key.Modifiers & ConsoleModifiers.Alt) == ConsoleModifiers.Alt,
			layout,
			out command
		);
	}

	private static bool TryMapHotkeyChar(
		char keyChar,
		bool hasControl,
		bool hasAlt,
		TerminalLayout layout,
		out string? command
	)
	{
		command = null;
		if (hasControl || hasAlt)
		{
			return false;
		}

		switch (keyChar)
		{
			case 'q':
			case '?':
			case 'a':
			case 'm':
			case 'z':
			case 'x':
			case 'h':
			case 'j':
			case 'k':
			case 'l':
			case 'f':
				command = keyChar.ToString();
				return true;
			case 'o':
				command = "optimize-preview";
				return true;
			case 't':
			case 'b':
			case 'r':
			case 's':
			case 'd':
				WriteStatus(layout, "Press Esc to enter commands that need values or file names.");
				return false;
			default:
				if (!char.IsControl(keyChar))
				{
					WriteStatus(layout, "Hotkeys are active; press Esc for command input.");
				}

				return false;
		}
	}

	private static string ReadPendingEscapeSequence()
	{
		var builder = new StringBuilder();
		builder.Append('\u001b');
		if (!TryReadPendingInputChar(builder, timeoutMilliseconds: 120))
		{
			return builder.ToString();
		}

		if (builder.Length == 2 && builder[1] != '[')
		{
			return builder.ToString();
		}

		long deadline = Environment.TickCount64 + 750;
		while (builder.Length < 64 && Environment.TickCount64 < deadline)
		{
			if (IsCompleteMouseSequence(builder) || IsCompleteControlSequence(builder))
			{
				break;
			}

			if (!TryReadPendingInputChar(builder, timeoutMilliseconds: 80))
			{
				break;
			}
		}

		return builder.ToString();
	}

	private static bool TryReadPendingInputChar(
		StringBuilder builder,
		int timeoutMilliseconds
	)
	{
		long deadline = Environment.TickCount64 + timeoutMilliseconds;
		while (Environment.TickCount64 < deadline)
		{
			if (Console.KeyAvailable)
			{
				builder.Append(Console.ReadKey(intercept: true).KeyChar);
				return true;
			}

			System.Threading.Thread.Sleep(5);
		}

		return false;
	}

	private static bool IsCompleteMouseSequence(StringBuilder builder)
	{
		return builder.Length > 3
			&& builder[0] == '\u001b'
			&& builder[1] == '['
			&& builder[2] == '<'
			&& (builder[^1] == 'M' || builder[^1] == 'm');
	}

	private static bool IsMouseSequencePrefix(string sequence)
	{
		return sequence.Length >= 3
			&& sequence[0] == '\u001b'
			&& sequence[1] == '['
			&& sequence[2] == '<';
	}

	private static bool IsCompleteControlSequence(StringBuilder builder)
	{
		if (builder.Length <= 2
			|| builder[0] != '\u001b'
			|| builder[1] != '['
			|| (builder.Length > 2 && builder[2] == '<'))
		{
			return false;
		}

		char final = builder[^1];
		return final >= '@' && final <= '~';
	}

	private static bool TryParseMouseEvent(
		string sequence,
		out MouseInputEvent? mouseEvent
	)
	{
		mouseEvent = null;
		if (!sequence.StartsWith("\u001b[<", StringComparison.Ordinal))
		{
			return false;
		}

		int finalIndex = sequence.IndexOf('M', 3);
		if (finalIndex < 0)
		{
			finalIndex = sequence.IndexOf('m', 3);
		}

		if (finalIndex < 0)
		{
			return false;
		}

		string[] fields = sequence.Substring(3, finalIndex - 3).Split(';');
		if (fields.Length != 3
			|| !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int button)
			|| !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int column)
			|| !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int row))
		{
			return false;
		}

		mouseEvent = new MouseInputEvent
		{
			Button = button,
			Column = column,
			Row = row,
			Released = sequence[finalIndex] == 'm',
		};
		return true;
	}

	private static bool HandleMouseEvent(
		MouseInputEvent mouseEvent,
		List<SliderHitBox> sliderHitBoxes,
		Gif320ConversionOptions options,
		ref SliderKind? activeSlider
	)
	{
		if ((mouseEvent.Button & 64) == 64)
		{
			return false;
		}

		if (mouseEvent.Released)
		{
			activeSlider = null;
			return false;
		}

		SliderHitBox? hitBox = FindSliderHitBox(mouseEvent, sliderHitBoxes);
		if (hitBox == null && activeSlider.HasValue)
		{
			hitBox = FindSliderHitBox(activeSlider.Value, sliderHitBoxes);
		}

		if (hitBox == null)
		{
			return false;
		}

		activeSlider = hitBox.Kind;
		if (IsLeftArrowHit(hitBox, mouseEvent.Column))
		{
			return AdjustSliderValue(hitBox, options, -1);
		}

		if (IsRightArrowHit(hitBox, mouseEvent.Column))
		{
			return AdjustSliderValue(hitBox, options, 1);
		}

		return SetSliderValueFromColumn(hitBox, options, mouseEvent.Column);
	}

	private static SliderHitBox? FindSliderHitBox(
		MouseInputEvent mouseEvent,
		List<SliderHitBox> sliderHitBoxes
	)
	{
		foreach (SliderHitBox hitBox in sliderHitBoxes)
		{
			if (mouseEvent.Row != hitBox.Row)
			{
				continue;
			}

			if (IsSliderColumnHit(hitBox, mouseEvent.Column))
			{
				return hitBox;
			}
		}

		return null;
	}

	private static bool IsSliderColumnHit(SliderHitBox hitBox, int column)
	{
		return IsLeftArrowHit(hitBox, column)
			|| IsRightArrowHit(hitBox, column)
			|| (column >= hitBox.BarStartColumn && column <= hitBox.BarEndColumn);
	}

	private static bool IsLeftArrowHit(SliderHitBox hitBox, int column)
	{
		return column >= hitBox.LeftArrowColumn - 1
			&& column <= hitBox.LeftArrowColumn + 1;
	}

	private static bool IsRightArrowHit(SliderHitBox hitBox, int column)
	{
		return column >= hitBox.RightArrowColumn - 1
			&& column <= hitBox.RightArrowColumn + 1;
	}

	private static SliderHitBox? FindSliderHitBox(
		SliderKind kind,
		List<SliderHitBox> sliderHitBoxes
	)
	{
		foreach (SliderHitBox hitBox in sliderHitBoxes)
		{
			if (hitBox.Kind == kind)
			{
				return hitBox;
			}
		}

		return null;
	}

	private static bool AdjustSliderValue(
		SliderHitBox hitBox,
		Gif320ConversionOptions options,
		int delta
	)
	{
		int current = GetSliderValue(hitBox.Kind, options);
		int next = Math.Clamp(current + delta, hitBox.Minimum, hitBox.Maximum);
		return SetSliderValue(hitBox.Kind, next, options);
	}

	private static bool SetSliderValueFromColumn(
		SliderHitBox hitBox,
		Gif320ConversionOptions options,
		int column
	)
	{
		int clampedColumn = Math.Clamp(
			column,
			hitBox.BarStartColumn,
			hitBox.BarEndColumn
		);
		int barWidth = Math.Max(1, hitBox.BarEndColumn - hitBox.BarStartColumn);
		int next = hitBox.Minimum + (int)Math.Round(
			(double)(clampedColumn - hitBox.BarStartColumn)
				* (hitBox.Maximum - hitBox.Minimum)
				/ barWidth
		);
		return SetSliderValue(hitBox.Kind, next, options);
	}

	private static int GetSliderValue(SliderKind kind, Gif320ConversionOptions options)
	{
		return kind switch
		{
			SliderKind.FullThreshold => options.FullThreshold,
			SliderKind.HalfThreshold => options.HalfThreshold,
			SliderKind.RedBalance => options.RedBalance,
			SliderKind.GreenBalance => options.GreenBalance,
			SliderKind.BlueBalance => options.BlueBalance,
			_ => 0,
		};
	}

	private static bool SetSliderValue(
		SliderKind kind,
		int value,
		Gif320ConversionOptions options
	)
	{
		int oldFull = options.FullThreshold;
		int oldHalf = options.HalfThreshold;
		int oldRed = options.RedBalance;
		int oldGreen = options.GreenBalance;
		int oldBlue = options.BlueBalance;

		switch (kind)
		{
			case SliderKind.FullThreshold:
				options.FullThreshold = Math.Clamp(value, 0, 100);
				options.HalfThreshold = Math.Min(
					options.HalfThreshold,
					options.FullThreshold
				);
				break;
			case SliderKind.HalfThreshold:
				options.HalfThreshold = Math.Clamp(
					value,
					0,
					Math.Min(100, options.FullThreshold)
				);
				break;
			case SliderKind.RedBalance:
				options.RedBalance = Math.Max(0, value);
				break;
			case SliderKind.GreenBalance:
				options.GreenBalance = Math.Max(0, value);
				break;
			case SliderKind.BlueBalance:
				options.BlueBalance = Math.Max(0, value);
				break;
		}

		bool changed = oldFull != options.FullThreshold
			|| oldHalf != options.HalfThreshold
			|| oldRed != options.RedBalance
			|| oldGreen != options.GreenBalance
			|| oldBlue != options.BlueBalance;
		if (changed)
		{
			options.AutoTune = false;
			options.ToneSettingsOverride = null;
		}

		return changed;
	}

	private static string? ReadCommandLineWithResize(
		PreviewMode previewMode,
		ref TerminalLayout layout,
		Action redraw
	)
	{
		if (Console.IsInputRedirected)
		{
			return Console.ReadLine();
		}

		var buffer = new StringBuilder();
		while (true)
		{
			TerminalLayout currentLayout = TerminalLayout.Create(previewMode);
			if (!currentLayout.SameAs(layout))
			{
				layout = currentLayout;
				redraw();
				WritePrompt(previewMode, layout);
				Console.Write(buffer.ToString());
			}

			if (!Console.KeyAvailable)
			{
				System.Threading.Thread.Sleep(50);
				continue;
			}

			ConsoleKeyInfo key = Console.ReadKey(intercept: true);
			if (key.Key == ConsoleKey.Enter)
			{
				Console.WriteLine();
				return buffer.ToString();
			}

			if (key.Key == ConsoleKey.Backspace)
			{
				if (buffer.Length > 0)
				{
					buffer.Length--;
					Console.Write("\b \b");
				}

				continue;
			}

			if (key.Key == ConsoleKey.C
				&& (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
			{
				return null;
			}

			if (!char.IsControl(key.KeyChar))
			{
				buffer.Append(key.KeyChar);
				Console.Write(key.KeyChar);
			}
		}
	}

	private static void WritePrompt(PreviewMode previewMode, TerminalLayout layout)
	{
		MoveCursor(layout.PromptRow, 1);
		Console.Write(ClearToEndOfLine);
		Console.Write(previewMode == PreviewMode.Full80x24
			? "GIF320 80x24> "
			: Prompt);
	}

	private static void WriteHotkeyPrompt(PreviewMode previewMode, TerminalLayout layout)
	{
		MoveCursor(layout.PromptRow, 1);
		Console.Write(ClearToEndOfLine);
		Console.Write(Clip(
			previewMode == PreviewMode.Full80x24
				? "GIF320 80x24 hotkeys active. Esc command input."
				: "GIF320 hotkeys active. Esc command input.",
			layout.Width
		));
	}

	private static void WriteAt(int row, int column, string text)
	{
		MoveCursor(row, column);
		Console.Write(text);
	}

	private static void MoveCursor(int row, int column)
	{
		Console.Write("\u001b[");
		Console.Write(row.ToString(CultureInfo.InvariantCulture));
		Console.Write(';');
		Console.Write(column.ToString(CultureInfo.InvariantCulture));
		Console.Write('f');
	}

	private static void AppendCursorMove(StringBuilder builder, int row, int column)
	{
		builder.Append("\u001b[");
		builder.Append(row.ToString(CultureInfo.InvariantCulture));
		builder.Append(';');
		builder.Append(column.ToString(CultureInfo.InvariantCulture));
		builder.Append('f');
	}

	private static int GetConsoleDimension(bool isWidth, int fallback)
	{
		try
		{
			int value = isWidth ? Console.WindowWidth : Console.WindowHeight;
			return value > 0 ? value : fallback;
		}
		catch (IOException)
		{
			return fallback;
		}
		catch (InvalidOperationException)
		{
			return fallback;
		}
		catch (PlatformNotSupportedException)
		{
			return fallback;
		}
	}

	private static bool IsModernTerminal()
	{
		if (Console.IsOutputRedirected)
		{
			return false;
		}

		if (OperatingSystem.IsWindows())
		{
			return true;
		}

		if (HasEnvironmentValue("WT_SESSION")
			|| HasEnvironmentValue("TERM_PROGRAM")
			|| HasEnvironmentValue("COLORTERM")
			|| HasEnvironmentValue("VTE_VERSION")
			|| HasEnvironmentValue("KONSOLE_VERSION"))
		{
			return true;
		}

		string term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
		string lower = term.ToLowerInvariant();
		string[] modernTerms =
		[
			"xterm",
			"screen",
			"tmux",
			"rxvt",
			"alacritty",
			"kitty",
			"wezterm",
			"vte",
			"gnome",
			"konsole",
			"iterm",
			"cygwin",
			"msys",
		];
		foreach (string marker in modernTerms)
		{
			if (lower.Contains(marker, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static bool HasEnvironmentValue(string name)
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
	}

	private static uint? TryEnableInteractiveInput(out bool useStreamInput)
	{
		useStreamInput = false;
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		if (!TryGetConsoleInputHandle(out IntPtr handle))
 		{
 			return null;
 		}
 
		if (!GetConsoleMode(handle, out uint mode))
		{
			return null;
		}

		uint updatedMode = mode
			| EnableExtendedFlags
			| EnableVirtualTerminalInput;
		updatedMode &= ~(EnableLineInput
			| EnableEchoInput
			| EnableQuickEditMode
			| EnableMouseInput);
		if (updatedMode != mode && !SetConsoleMode(handle, updatedMode))
		{
			return null;
		}

		useStreamInput = true;
		return mode;
	}

	private static void RestoreInteractiveInput(uint? originalMode)
	{
		if (!originalMode.HasValue || !OperatingSystem.IsWindows())
		{
			return;
		}

		if (!TryGetConsoleInputHandle(out IntPtr handle))
		{
			return;
		}

		SetConsoleMode(handle, originalMode.Value);
	}

	private static bool TryGetConsoleInputHandle(out IntPtr handle)
	{
		handle = GetStdHandle(StandardInputHandle);
		return handle != IntPtr.Zero && handle != new IntPtr(-1);
	}

	private static bool WaitForInput(IntPtr handle, int timeoutMilliseconds)
	{
		uint result = WaitForSingleObject(handle, (uint)Math.Max(0, timeoutMilliseconds));
		return result == WaitObject0;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	private static string Clip(string value, int width)
	{
		if (width <= 0)
		{
			return string.Empty;
		}

		return value.Length <= width ? value : value.Substring(0, width);
	}

	private static string FormatFixed(string value, int width)
	{
		if (value.Length > width)
		{
			return value.Substring(value.Length - width, width);
		}

		return value.PadLeft(width);
	}

	private static string TruncateLeft(string value, int width)
	{
		if (value.Length <= width)
		{
			return value;
		}

		return value.Substring(value.Length - width, width);
	}

	private static void WriteFile(
		Gif320Converter converter,
		string inputPath,
		string outputPath,
		Gif320ConversionOptions options
	)
	{
		Gif320RenderResult result = converter.RenderGifFile(inputPath, options);
		File.WriteAllText(outputPath, result.VtSequence, Encoding.ASCII);
	}

	private static CliOptions ParseArgs(string[] args)
	{
		var cli = new CliOptions();
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			switch (arg)
			{
				case "-p":
					cli.PipeMode = true;
					break;
				case "-h":
				case "--help":
				case "/?":
					cli.ShowHelp = true;
					break;
				case "--full-screen":
				case "--fullscreen":
					cli.Conversion.FullScreenDouble = true;
					break;
				case "--double":
					cli.Conversion.DoubleSize = true;
					break;
				case "--no-auto":
					cli.Conversion.AutoTune = false;
					break;
				case "--no-reduce":
					cli.Conversion.AllowGlyphReduction = false;
					break;
				case "--interactive-compat":
				case "--interactive-compatible":
				case "--old-interactive":
					cli.InteractiveCompatibilityMode = true;
					break;
				case "--no-optimize":
				case "--no-optimise":
					cli.Conversion.OptimizeSize = false;
					break;
				case "--output":
				case "-o":
					cli.OutputPath = RequireValue(args, ref i, arg);
					break;
				case "--cells":
					cli.Conversion.CellsX = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.CellsY = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.OptimizeSize = false;
					break;
				case "--threshold":
				case "--thresholds":
					cli.Conversion.FullThreshold = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.HalfThreshold = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.AutoTune = false;
					break;
				case "--balance":
					cli.Conversion.RedBalance = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.GreenBalance = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.BlueBalance = int.Parse(RequireValue(args, ref i, arg));
					cli.Conversion.AutoTune = false;
					break;
				case "--ratio":
					cli.Conversion.Ratio = double.Parse(RequireValue(args, ref i, arg));
					break;
				default:
					if (arg.StartsWith("-", StringComparison.Ordinal))
					{
						throw new ArgumentException("unknown option " + arg);
					}

					cli.Files.Add(arg);
					break;
			}
		}

		return cli;
	}

	private static string RequireValue(string[] args, ref int index, string option)
	{
		index++;
		if (index >= args.Length)
		{
			throw new ArgumentException(option + " requires a value");
		}

		return args[index];
	}

	private static void WriteSequence(Stream output, string sequence)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(sequence);
		output.Write(bytes, 0, bytes.Length);
	}

	private static bool UseOriginalPipeRenderer(Gif320ConversionOptions options)
	{
		return !options.FullScreenDouble
			&& !options.DoubleSize
			&& !options.CellsX.HasValue
			&& !options.CellsY.HasValue
			&& options.OptimizeSize
			&& options.AllowGlyphReduction;
	}

	private static void Usage(TextWriter writer)
	{
		writer.WriteLine("usage: gif320 -p < giffile");
		writer.WriteLine("or:    gif320 <inputfile> ...");
		writer.WriteLine();
		writer.WriteLine("extra managed options:");
		writer.WriteLine("  --output <file>       render non-interactively");
		writer.WriteLine("  --full-screen         render 40x12 double-width/double-height");
		writer.WriteLine("  --double              use double-width/double-height line attributes");
		writer.WriteLine("  --cells <x> <y>       render a specific logical cell size");
		writer.WriteLine("  --threshold <f> <h>   set original GIF320 thresholds and disable auto tune");
		writer.WriteLine("  --balance <r> <g> <b> set original GIF320 balance and disable auto tune");
		writer.WriteLine("  --ratio <ratio>       set optimisation aspect ratio");
		writer.WriteLine("  --no-auto             disable automatic image tuning");
		writer.WriteLine("  --interactive-compat  use old deterministic interactive startup");
		writer.WriteLine("  --no-reduce           fail instead of quantizing when cells exceed glyph budget");
	}

	private sealed class CliOptions
	{
		public bool PipeMode { get; set; }

		public bool ShowHelp { get; set; }

		public bool InteractiveCompatibilityMode { get; set; }

		public string OutputPath { get; set; } = string.Empty;

		public Gif320ConversionOptions Conversion { get; } = new();

		public List<string> Files { get; } = new();
	}
}
