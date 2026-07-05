using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gif320Sharp_Core
{
	public sealed class Gif320Renderer
	{
		private const int BitsPerGlyph = Gif320RenderOptions.CellPixelWidth
			* Gif320RenderOptions.CellPixelHeight;
		private const int BytesPerGlyph = (BitsPerGlyph + 7) / 8;
		private const string AtlasPrefix = "gif320-atlas-v1:";
		private const string CellMapPrefix = "gif320-map-v1:";
		private const int MaxAutoTuneSearchPixels = 60000;
		private const int MaxWorstCellRefinementIterations = 8;
		private const int MaxWorstCellRefinementCandidates = 18;

		private static readonly double[] SrgbToLinear = CreateSrgbToLinearTable();
		private static readonly ulong[] GlyphBitMasks = CreateGlyphBitMasks();

		public Gif320RenderResult RenderRgb(
			byte[] rgb,
			int width,
			int height
		)
		{
			return Render(rgb, width, height, Gif320PixelFormat.Rgb24, new Gif320RenderOptions());
		}

		public Gif320RenderResult RenderRgb(
			byte[] rgb,
			int width,
			int height,
			Gif320RenderOptions options,
			CancellationToken cancellationToken = default
		)
		{
			return Render(rgb, width, height, Gif320PixelFormat.Rgb24, options, cancellationToken);
		}

		public Gif320RenderResult RenderRgba(
			byte[] rgba,
			int width,
			int height,
			Gif320RenderOptions options,
			CancellationToken cancellationToken = default
		)
		{
			return Render(rgba, width, height, Gif320PixelFormat.Rgba32, options, cancellationToken);
		}

		public Gif320RenderResult Render(
			ReadOnlySpan<byte> pixels,
			int width,
			int height,
			Gif320PixelFormat pixelFormat,
			Gif320RenderOptions options,
			CancellationToken cancellationToken = default
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (width <= 0 || height <= 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(width),
					"Image dimensions must be positive."
				);
			}

			if (pixels.Length < checked(width * height * BytesPerPixel(pixelFormat)))
			{
				throw new ArgumentException(
					"Pixel buffer is too small for the supplied dimensions and format.",
					nameof(pixels)
				);
			}

			Gif320RenderOptions workingOptions = options.Clone();
			workingOptions.Validate();

			LinearImage image = ResizeToLinearImage(
				pixels,
				width,
				height,
				pixelFormat,
				workingOptions
			);
			cancellationToken.ThrowIfCancellationRequested();

			if (!workingOptions.AutoTune)
			{
				Gif320ToneSettings settings = workingOptions.ToneSettings.Clone();
				settings.NormalizeColorWeights();
				ApplyAutomaticThresholdsIfNeeded(image, settings);
				return RenderWithSettings(
					image,
					workingOptions,
					settings,
					reduceGlyphs: true,
					cancellationToken: cancellationToken
				);
			}

			return RenderWithAutomaticSettings(image, workingOptions, cancellationToken);
		}

		private static Gif320RenderResult RenderWithAutomaticSettings(
			LinearImage image,
			Gif320RenderOptions options,
			CancellationToken cancellationToken
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidates = new List<Gif320ToneSettings>();
			foreach (Gif320ToneSettings settings in EnumerateAutoSettings(image, options))
			{
				candidates.Add(settings);
			}

			LinearImage searchImage = CreateAutoTuneSearchImage(image);
			var finalists = new List<ScoredSettings>();
			var preliminary = new ScoredSettings[candidates.Count];
			Parallel.For(0, candidates.Count, new ParallelOptions
			{
				CancellationToken = cancellationToken,
			}, i =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				preliminary[i] = ScoreSettingsFast(searchImage, candidates[i], options);
			});

			int packedCandidateLimit = Math.Min(
				candidates.Count,
				Math.Max(options.AutoTuneFinalists * 6, 48)
			);
			var packedCandidates = new List<ScoredSettings>();
			foreach (ScoredSettings candidate in preliminary)
			{
				InsertFinalist(packedCandidates, candidate, packedCandidateLimit);
			}

			var scored = new ScoredSettings[packedCandidates.Count];
			Parallel.For(0, packedCandidates.Count, new ParallelOptions
			{
				CancellationToken = cancellationToken,
			}, i =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				scored[i] = ScoreSettings(
					image,
					options,
					packedCandidates[i].Settings,
					cancellationToken
				);
			});

			foreach (ScoredSettings candidate in scored)
			{
				InsertFinalist(
					finalists,
					candidate,
					options.AutoTuneFinalists
				);
			}

			var results = new Gif320RenderResult[finalists.Count];
			Parallel.For(0, finalists.Count, new ParallelOptions
			{
				CancellationToken = cancellationToken,
			}, i =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				results[i] = RenderWithSettings(
					image,
					options,
					finalists[i].Settings,
					reduceGlyphs: true,
					cancellationToken: cancellationToken
				);
			});

			Gif320RenderResult best = results[0];
			double bestScore = double.NegativeInfinity;
			foreach (Gif320RenderResult result in results)
			{
				if (result.Score > bestScore)
				{
					bestScore = result.Score;
					best = result;
				}
			}

			return best;
		}

		private static ScoredSettings ScoreSettingsFast(
			LinearImage image,
			Gif320ToneSettings settings,
			Gif320RenderOptions options
		)
		{
			RenderedBitmap rendered = RenderBitmap(image, settings);
			double score = ScoreImage(
				rendered.Reference,
				rendered.Bitmap,
				image.Width,
				image.Height,
				reductionErrorPerCellPixel: 0.0,
				reductionHighErrorPerCellPixel: 0.0,
				reductionWorstErrorPerCellPixel: 0.0,
				glyphPressurePenalty: 0.0,
				options
			);
			return new ScoredSettings(settings, score);
		}

		private static ScoredSettings ScoreSettings(
			LinearImage image,
			Gif320RenderOptions options,
			Gif320ToneSettings settings,
			CancellationToken cancellationToken
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RenderedBitmap rendered = RenderBitmap(image, settings);
			PackedScreen packed = PackCells(
				rendered.Bitmap,
				image.Width,
				image.Height,
				options,
				reduceGlyphs: false,
				cancellationToken
			);
			ApplyManualAtlas(packed, options.ManualAtlas);
			ApplyManualCellMap(packed, options.ManualAtlas, options.ManualCellMap, options.CellsX, options.CellsY);
			bool[] reconstructed = ReconstructBitmap(
				packed,
				options.CellsX,
				options.CellsY
			);
			double score = ScoreImage(
				rendered.Reference,
				reconstructed,
				image.Width,
				image.Height,
				packed.ReductionErrorPerCellPixel,
				packed.HighReductionErrorPerCellPixel,
				packed.WorstReductionErrorPerCellPixel,
				GetGlyphPressurePenalty(packed.Glyphs.Count, options),
				options
			);
			return new ScoredSettings(settings, score);
		}

		private static Gif320RenderResult RenderWithSettings(
			LinearImage image,
			Gif320RenderOptions options,
			Gif320ToneSettings settings,
			bool reduceGlyphs,
			CancellationToken cancellationToken
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RenderedBitmap rendered = RenderBitmap(image, settings);
			PackedScreen packed = PackCells(
				rendered.Bitmap,
				image.Width,
				image.Height,
				options,
				reduceGlyphs,
				cancellationToken
			);
			ApplyManualAtlas(packed, options.ManualAtlas);
			ApplyManualCellMap(packed, options.ManualAtlas, options.ManualCellMap, options.CellsX, options.CellsY);
			bool[] reconstructed = ReconstructBitmap(
				packed,
				options.CellsX,
				options.CellsY
			);
			double score = ScoreImage(
				rendered.Reference,
				reconstructed,
				image.Width,
				image.Height,
				packed.ReductionErrorPerCellPixel,
				packed.HighReductionErrorPerCellPixel,
				packed.WorstReductionErrorPerCellPixel,
				0.0,
				options
			);

			string[] rows = BuildScreenRows(packed, options.CellsX, options.CellsY);
			string sequence = BuildVtSequence(packed, rows, options);
			string glyphAtlas = BuildGlyphAtlas(packed.Glyphs);
			string cellMap = BuildCellMap(packed, options.CellsX, options.CellsY);
			return new Gif320RenderResult(
				sequence,
				rows,
				GetSixelPatterns(packed.Glyphs),
				packed.CellReverseVideo,
				settings.Clone(),
				options.CellsX,
				options.CellsY,
				packed.UniqueGlyphCount,
				glyphAtlas,
				cellMap,
				score,
				packed.ReductionErrorPerCellPixel,
				packed.HighReductionErrorPerCellPixel,
				packed.WorstReductionErrorPerCellPixel
			);
		}

		private static IEnumerable<Gif320ToneSettings> EnumerateAutoSettings(
			LinearImage image,
			Gif320RenderOptions options
		)
		{
			double[][] balances =
			{
				NormalizeBalance(
					options.ToneSettings.RedWeight,
					options.ToneSettings.GreenWeight,
					options.ToneSettings.BlueWeight
				),
				new[] { 0.2126, 0.7152, 0.0722 },
				new[] { 0.299, 0.587, 0.114 },
				new[] { 0.375, 0.5, 0.125 },
				new[] { 0.15, 0.75, 0.10 },
			};

			double[] gammas = { 0.85, 1.0, 1.2 };
			double[] contrasts = { 0.9, 1.1, 1.3 };
			double[] brightnesses = { -0.04, 0.0, 0.04 };

			var emitted = new HashSet<string>(StringComparer.Ordinal);
			foreach (double[] balance in balances)
			{
				foreach (double gamma in gammas)
				{
					foreach (double contrast in contrasts)
					{
						foreach (double brightness in brightnesses)
						{
							foreach (Gif320ToneSettings settings in CreateThresholdVariants(
								image,
								balance,
								gamma,
								contrast,
								brightness,
								useLocalContrast: false,
								options
							))
							{
								if (emitted.Add(GetSettingsKey(settings)))
								{
									yield return settings;
								}
							}
						}
					}
				}

				foreach (Gif320ToneSettings settings in CreateThresholdVariants(
					image,
					balance,
					gamma: 1.0,
					contrast: 1.0,
					brightness: 0.0,
					useLocalContrast: true,
					options
				))
				{
					if (emitted.Add(GetSettingsKey(settings)))
					{
						yield return settings;
					}
				}
			}
		}

		private static IEnumerable<Gif320ToneSettings> CreateThresholdVariants(
			LinearImage image,
			double[] balance,
			double gamma,
			double contrast,
			double brightness,
			bool useLocalContrast,
			Gif320RenderOptions options
		)
		{
			var baseSettings = new Gif320ToneSettings
			{
				RedWeight = balance[0],
				GreenWeight = balance[1],
				BlueWeight = balance[2],
				Gamma = gamma,
				Contrast = contrast,
				Brightness = brightness,
				UseLocalContrast = useLocalContrast,
				LocalContrastClipLimit = useLocalContrast ? 0.025 : 0.0,
			};

			double[] values = BuildToneValues(image, baseSettings);
			double otsu = OtsuThreshold(values);
			double balanced = (otsu + 0.5) * 0.5;

			yield return WithDither(
				baseSettings,
				Gif320DitherMode.FloydSteinberg,
				otsu,
				otsu * 0.5,
				options
			);
			yield return WithDither(
				baseSettings,
				Gif320DitherMode.FloydSteinberg,
				balanced,
				balanced * 0.5,
				options
			);
			yield return WithDither(
				baseSettings,
				Gif320DitherMode.Checkerboard,
				otsu,
				Math.Max(0.0, otsu * 0.55),
				options
			);
			yield return WithDither(
				baseSettings,
				Gif320DitherMode.Checkerboard,
				0.5,
				0.25,
				options
			);
			yield return WithDither(
				baseSettings,
				Gif320DitherMode.Threshold,
				otsu,
				otsu,
				options
			);
		}

		private static Gif320ToneSettings WithDither(
			Gif320ToneSettings baseSettings,
			Gif320DitherMode ditherMode,
			double threshold,
			double halfThreshold,
			Gif320RenderOptions options
		)
		{
			Gif320ToneSettings settings = baseSettings.Clone();
			settings.DitherMode = ditherMode;
			settings.Threshold = Clamp01(threshold);
			settings.HalfThreshold = Math.Min(settings.Threshold, Clamp01(halfThreshold));
			ApplyAutoTuneLocks(settings, options);
			return settings;
		}

		private static void ApplyAutoTuneLocks(
			Gif320ToneSettings settings,
			Gif320RenderOptions options
		)
		{
			if ((options.AutoTuneLocks & Gif320AutoTuneLocks.Balance) != 0)
			{
				double[] candidate = NormalizeBalance(
					settings.RedWeight,
					settings.GreenWeight,
					settings.BlueWeight
				);
				double[] locked = NormalizeBalance(
					options.ToneSettings.RedWeight,
					options.ToneSettings.GreenWeight,
					options.ToneSettings.BlueWeight
				);
				double[] balance = ApplyLockedBalance(candidate, locked, options.AutoTuneLocks);
				settings.RedWeight = balance[0];
				settings.GreenWeight = balance[1];
				settings.BlueWeight = balance[2];
			}

			bool lockFullThreshold =
				(options.AutoTuneLocks & Gif320AutoTuneLocks.FullThreshold) != 0;
			bool lockHalfThreshold =
				(options.AutoTuneLocks & Gif320AutoTuneLocks.HalfThreshold) != 0;
			if (!lockFullThreshold && !lockHalfThreshold)
			{
				return;
			}

			if (lockFullThreshold)
			{
				settings.Threshold = Clamp01(options.ToneSettings.Threshold);
			}

			if (lockHalfThreshold)
			{
				settings.HalfThreshold = Clamp01(options.ToneSettings.HalfThreshold);
			}

			if (settings.HalfThreshold > settings.Threshold)
			{
				if (lockHalfThreshold && !lockFullThreshold)
				{
					settings.Threshold = settings.HalfThreshold;
				}
				else
				{
					settings.HalfThreshold = settings.Threshold;
				}
			}
		}

		private static double[] ApplyLockedBalance(
			double[] candidate,
			double[] locked,
			Gif320AutoTuneLocks locks
		)
		{
			bool lockRed = (locks & Gif320AutoTuneLocks.RedBalance) != 0;
			bool lockGreen = (locks & Gif320AutoTuneLocks.GreenBalance) != 0;
			bool lockBlue = (locks & Gif320AutoTuneLocks.BlueBalance) != 0;
			var result = new double[3];
			bool[] lockedChannels = { lockRed, lockGreen, lockBlue };
			double lockedSum = 0.0;
			double freeCandidateSum = 0.0;
			int freeCount = 0;

			for (int i = 0; i < result.Length; i++)
			{
				if (lockedChannels[i])
				{
					result[i] = locked[i];
					lockedSum += locked[i];
				}
				else
				{
					freeCandidateSum += candidate[i];
					freeCount++;
				}
			}

			if (freeCount == 0)
			{
				return result;
			}

			double remaining = Math.Max(0.0, 1.0 - lockedSum);
			for (int i = 0; i < result.Length; i++)
			{
				if (lockedChannels[i])
				{
					continue;
				}

				result[i] = freeCandidateSum > 0.0
					? candidate[i] * remaining / freeCandidateSum
					: remaining / freeCount;
			}

			return result;
		}

		private static RenderedBitmap RenderBitmap(
			LinearImage image,
			Gif320ToneSettings settings
		)
		{
			Gif320ToneSettings working = settings.Clone();
			working.NormalizeColorWeights();
			double[] reference = BuildReferenceLuminance(image, working);
			double[] values = BuildToneValues(reference, image.Width, image.Height, working);
			bool[] bitmap = Dither(values, image.Width, image.Height, working);
			return new RenderedBitmap(reference, bitmap);
		}

		private static double[] BuildReferenceLuminance(
			LinearImage image,
			Gif320ToneSettings settings
		)
		{
			double[] values = new double[image.Width * image.Height];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = Clamp01(
					image.Red[i] * settings.RedWeight
					+ image.Green[i] * settings.GreenWeight
					+ image.Blue[i] * settings.BlueWeight
				);
			}

			return values;
		}

		private static double[] BuildToneValues(
			LinearImage image,
			Gif320ToneSettings settings
		)
		{
			return BuildToneValues(
				BuildReferenceLuminance(image, settings),
				image.Width,
				image.Height,
				settings
			);
		}

		private static double[] BuildToneValues(
			double[] reference,
			int width,
			int height,
			Gif320ToneSettings settings
		)
		{
			double[] values = new double[reference.Length];
			Array.Copy(reference, values, reference.Length);
			double gamma = Math.Max(0.05, settings.Gamma);
			for (int i = 0; i < values.Length; i++)
			{
				double value = ((values[i] - 0.5) * settings.Contrast)
					+ 0.5
					+ settings.Brightness;
				value = Clamp01(value);
				values[i] = Clamp01(Math.Pow(value, 1.0 / gamma));
			}

			if (settings.UseLocalContrast)
			{
				values = ApplyClahe(
					values,
					width,
					height,
					tilesX: 8,
					tilesY: 4,
					settings.LocalContrastClipLimit
				);
			}

			return values;
		}

		private static void ApplyAutomaticThresholdsIfNeeded(
			LinearImage image,
			Gif320ToneSettings settings
		)
		{
			if (settings.Threshold > 0.0 && settings.Threshold < 1.0)
			{
				settings.HalfThreshold = Math.Min(
					settings.Threshold,
					Clamp01(settings.HalfThreshold)
				);
				return;
			}

			double[] values = BuildToneValues(image, settings);
			settings.Threshold = OtsuThreshold(values);
			settings.HalfThreshold = settings.Threshold * 0.55;
		}

		private static bool[] Dither(
			double[] values,
			int width,
			int height,
			Gif320ToneSettings settings
		)
		{
			var output = new bool[values.Length];
			switch (settings.DitherMode)
			{
				case Gif320DitherMode.Threshold:
					for (int i = 0; i < values.Length; i++)
					{
						output[i] = values[i] >= settings.Threshold;
					}
					break;

				case Gif320DitherMode.Checkerboard:
					for (int y = 0; y < height; y++)
					{
						int rowOffset = y * width;
						for (int x = 0; x < width; x++)
						{
							double value = values[rowOffset + x];
							output[rowOffset + x] = value >= settings.Threshold
								|| (value >= settings.HalfThreshold && ((x + y) & 1) == 0);
						}
					}
					break;

				case Gif320DitherMode.FloydSteinberg:
					DitherFloydSteinberg(values, width, height, settings.Threshold, output);
					break;

				default:
					throw new ArgumentOutOfRangeException(nameof(settings));
			}

			return output;
		}

		private static void DitherFloydSteinberg(
			double[] values,
			int width,
			int height,
			double threshold,
			bool[] output
		)
		{
			double[] work = new double[values.Length];
			Array.Copy(values, work, values.Length);
			for (int y = 0; y < height; y++)
			{
				int rowOffset = y * width;
				for (int x = 0; x < width; x++)
				{
					int index = rowOffset + x;
					double oldValue = work[index];
					double newValue = oldValue >= threshold ? 1.0 : 0.0;
					output[index] = newValue > 0.5;
					double error = oldValue - newValue;

					if (x + 1 < width)
					{
						work[index + 1] += error * 7.0 / 16.0;
					}

					if (y + 1 >= height)
					{
						continue;
					}

					int nextRow = index + width;
					if (x > 0)
					{
						work[nextRow - 1] += error * 3.0 / 16.0;
					}

					work[nextRow] += error * 5.0 / 16.0;
					if (x + 1 < width)
					{
						work[nextRow + 1] += error / 16.0;
					}
				}
			}
		}

		private static PackedScreen PackCells(
			bool[] bitmap,
			int bitmapWidth,
			int bitmapHeight,
			Gif320RenderOptions options,
			bool reduceGlyphs,
			CancellationToken cancellationToken
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (bitmapWidth != options.CellsX * Gif320RenderOptions.CellPixelWidth
				|| bitmapHeight != options.CellsY * Gif320RenderOptions.CellPixelHeight)
			{
				throw new ArgumentException("Bitmap size does not match output cell dimensions.");
			}

			var unique = new List<GlyphPattern>();
			var byKey = new Dictionary<GlyphKey, int>();
			int[] cellUniqueIndexes = new int[options.CellsX * options.CellsY];
			for (int cellY = 0; cellY < options.CellsY; cellY++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				for (int cellX = 0; cellX < options.CellsX; cellX++)
				{
					bool[] pixels = ExtractCell(bitmap, bitmapWidth, cellX, cellY);
					ulong[] packedBits = PackBits(pixels);
					var key = new GlyphKey(packedBits);
					if (!byKey.TryGetValue(key, out int uniqueIndex))
					{
						uniqueIndex = unique.Count;
						unique.Add(new GlyphPattern(pixels, packedBits));
						byKey.Add(key, uniqueIndex);
					}

					unique[uniqueIndex].Weight++;
					cellUniqueIndexes[cellY * options.CellsX + cellX] = uniqueIndex;
				}
			}

			PackedScreen exact = BuildReverseVideoPackedScreen(
				unique,
				cellUniqueIndexes,
				unique.Count,
				reductionErrorPerCellPixel: 0.0,
				highReductionErrorPerCellPixel: 0.0,
				worstReductionErrorPerCellPixel: 0.0,
				options
			);
			if (exact.Glyphs.Count <= options.MaxGlyphs)
			{
				return exact;
			}

			if (!reduceGlyphs)
			{
				return exact;
			}

			if (options.GlyphReductionMode == Gif320GlyphReductionMode.Exact)
			{
				throw new InvalidOperationException(
					$"The image needs {unique.Count} glyphs, exceeding the configured budget of {options.MaxGlyphs}."
				);
			}

			VectorQuantizationResult reduced = ReduceWithVectorQuantization(
				unique,
				options.MaxGlyphs,
				options.MaxReductionIterations,
				cancellationToken
			);
			int[] cellGlyphIndexes = new int[cellUniqueIndexes.Length];
			for (int i = 0; i < cellGlyphIndexes.Length; i++)
			{
				cellGlyphIndexes[i] = reduced.UniqueToGlyph[cellUniqueIndexes[i]];
			}

			return BuildReverseVideoPackedScreen(
				reduced.Glyphs,
				cellGlyphIndexes,
				unique.Count,
				reduced.ErrorPerCellPixel,
				reduced.HighErrorPerCellPixel,
				reduced.WorstErrorPerCellPixel,
				options
			);
		}

		private static PackedScreen BuildReverseVideoPackedScreen(
			IReadOnlyList<GlyphPattern> sourceGlyphs,
			int[] sourceCellGlyphIndexes,
			int uniqueGlyphCount,
			double reductionErrorPerCellPixel,
			double highReductionErrorPerCellPixel,
			double worstReductionErrorPerCellPixel,
			Gif320RenderOptions options
		)
		{
			var glyphWeights = new int[sourceGlyphs.Count];
			foreach (int glyphIndex in sourceCellGlyphIndexes)
			{
				if (glyphIndex >= 0)
				{
					glyphWeights[glyphIndex]++;
				}
			}

			var order = new int[sourceGlyphs.Count];
			for (int i = 0; i < order.Length; i++)
			{
				order[i] = i;
			}

			Array.Sort(order, (left, right) =>
			{
				int weightCompare = glyphWeights[right].CompareTo(glyphWeights[left]);
				return weightCompare != 0 ? weightCompare : left.CompareTo(right);
			});

			var outputGlyphs = new List<GlyphPattern>();
			var represented = new bool[sourceGlyphs.Count];
			var sourceToOutput = new int[sourceGlyphs.Count];
			var sourceToReverseVideo = new bool[sourceGlyphs.Count];
			Array.Fill(sourceToOutput, int.MinValue);

			int tolerance = Math.Max(0, options.ReverseVideoInversionTolerance);
			double extraError = 0.0;
			int maxReverseVideoDistance = 0;
			foreach (int sourceIndex in order)
			{
				if (represented[sourceIndex])
				{
					continue;
				}

				GlyphPattern glyph = sourceGlyphs[sourceIndex];
				if (IsBlankGlyph(glyph.PackedBits))
				{
					MapSourceGlyph(
						sourceIndex,
						outputGlyphIndex: -1,
						reverseVideo: false,
						represented,
						sourceToOutput,
						sourceToReverseVideo
					);
					continue;
				}

				if (IsFullGlyph(glyph.PackedBits))
				{
					MapSourceGlyph(
						sourceIndex,
						outputGlyphIndex: -1,
						reverseVideo: true,
						represented,
						sourceToOutput,
						sourceToReverseVideo
					);
					continue;
				}

				int outputGlyphIndex = outputGlyphs.Count;
				outputGlyphs.Add(glyph);
				MapSourceGlyph(
					sourceIndex,
					outputGlyphIndex,
					reverseVideo: false,
					represented,
					sourceToOutput,
					sourceToReverseVideo
				);

				foreach (int candidateIndex in order)
				{
					if (represented[candidateIndex])
					{
						continue;
					}

					GlyphPattern candidate = sourceGlyphs[candidateIndex];
					if (IsBlankGlyph(candidate.PackedBits) || IsFullGlyph(candidate.PackedBits))
					{
						continue;
					}

					int distance = InvertedHammingDistance(
						candidate.PackedBits,
						glyph.PackedBits
					);
					if (distance > tolerance)
					{
						continue;
					}

					MapSourceGlyph(
						candidateIndex,
						outputGlyphIndex,
						reverseVideo: true,
						represented,
						sourceToOutput,
						sourceToReverseVideo
					);
					extraError += distance * glyphWeights[candidateIndex];
					if (distance > maxReverseVideoDistance)
					{
						maxReverseVideoDistance = distance;
					}
				}
			}

			var cellGlyphIndexes = new int[sourceCellGlyphIndexes.Length];
			var cellReverseVideo = new bool[sourceCellGlyphIndexes.Length];
			for (int i = 0; i < sourceCellGlyphIndexes.Length; i++)
			{
				int sourceIndex = sourceCellGlyphIndexes[i];
				cellGlyphIndexes[i] = sourceToOutput[sourceIndex];
				cellReverseVideo[i] = sourceToReverseVideo[sourceIndex];
			}

			double cellPixels = Math.Max(1.0, sourceCellGlyphIndexes.Length * BitsPerGlyph);
			double reverseVideoWorstError = maxReverseVideoDistance / (double)BitsPerGlyph;
			double totalReductionError = Math.Min(
				1.0,
				reductionErrorPerCellPixel + (extraError / cellPixels)
			);
			double totalHighReductionError = Math.Max(
				highReductionErrorPerCellPixel,
				reverseVideoWorstError
			);
			double totalWorstReductionError = Math.Max(
				worstReductionErrorPerCellPixel,
				reverseVideoWorstError
			);
			return new PackedScreen(
				outputGlyphs,
				cellGlyphIndexes,
				cellReverseVideo,
				uniqueGlyphCount,
				totalReductionError,
				totalHighReductionError,
				totalWorstReductionError
			);
		}

		private static void MapSourceGlyph(
			int sourceIndex,
			int outputGlyphIndex,
			bool reverseVideo,
			bool[] represented,
			int[] sourceToOutput,
			bool[] sourceToReverseVideo
		)
		{
			represented[sourceIndex] = true;
			sourceToOutput[sourceIndex] = outputGlyphIndex;
			sourceToReverseVideo[sourceIndex] = reverseVideo;
		}

		private static bool IsBlankGlyph(ulong[] packedBits)
		{
			for (int i = 0; i < packedBits.Length; i++)
			{
				if ((packedBits[i] & GlyphBitMasks[i]) != 0UL)
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsFullGlyph(ulong[] packedBits)
		{
			for (int i = 0; i < packedBits.Length; i++)
			{
				if ((packedBits[i] & GlyphBitMasks[i]) != GlyphBitMasks[i])
				{
					return false;
				}
			}

			return true;
		}

		private static VectorQuantizationResult ReduceWithVectorQuantization(
			List<GlyphPattern> unique,
			int maxGlyphs,
			int maxIterations,
			CancellationToken cancellationToken
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int glyphCount = Math.Min(maxGlyphs, unique.Count);
			var centers = InitializeCodebook(unique, glyphCount);
			int[] assignments = new int[unique.Count];
			for (int i = 0; i < assignments.Length; i++)
			{
				assignments[i] = -1;
			}

			for (int iteration = 0; iteration < maxIterations; iteration++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				bool changed = AssignToNearestCenters(unique, centers, assignments, cancellationToken);
				UpdateCenters(unique, centers, assignments);
				if (!changed && iteration > 0)
				{
					break;
				}
			}

			AssignToNearestCenters(unique, centers, assignments, cancellationToken);
			ImproveWorstRepresentedCells(unique, centers, assignments, cancellationToken);
			AssignToNearestCenters(unique, centers, assignments, cancellationToken);
			ulong[][] centerBits = PackCenters(centers);
			ReductionErrorStats errorStats =
				ComputeReductionErrorStats(unique, centerBits, assignments);

			var glyphs = new List<GlyphPattern>(centers.Count);
			var centerToGlyph = new Dictionary<GlyphKey, int>();
			int[] uniqueToGlyph = new int[unique.Count];
			for (int i = 0; i < unique.Count; i++)
			{
				bool[] center = centers[assignments[i]];
				ulong[] packedBits = centerBits[assignments[i]];
				var key = new GlyphKey(packedBits);
				if (!centerToGlyph.TryGetValue(key, out int glyphIndex))
				{
					glyphIndex = glyphs.Count;
					glyphs.Add(new GlyphPattern(CopyPixels(center), packedBits));
					centerToGlyph.Add(key, glyphIndex);
				}

				uniqueToGlyph[i] = glyphIndex;
			}

			return new VectorQuantizationResult(
				glyphs,
				uniqueToGlyph,
				errorStats.Average,
				errorStats.High,
				errorStats.Worst
			);
		}

		private static void ImproveWorstRepresentedCells(
			List<GlyphPattern> unique,
			List<bool[]> centers,
			int[] assignments,
			CancellationToken cancellationToken
		)
		{
			if (unique.Count <= centers.Count || centers.Count <= 1)
			{
				return;
			}

			for (int iteration = 0; iteration < MaxWorstCellRefinementIterations; iteration++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				ulong[][] centerBits = PackCenters(centers);
				ReductionErrorStats current =
					ComputeReductionErrorStats(unique, centerBits, assignments);
				int worstUnique = FindWorstRepresentedPatternIndex(
					unique,
					centerBits,
					assignments
				);
				if (HammingDistance(unique[worstUnique].PackedBits, centerBits[assignments[worstUnique]]) == 0)
				{
					break;
				}

				int[] replacementCandidates = FindReplacementCandidates(
					unique,
					centerBits,
					MaxWorstCellRefinementCandidates
				);
				int[] bestAssignments = assignments;
				bool[]? bestReplacement = null;
				int bestCenter = -1;
				ReductionErrorStats best = current;

				foreach (int candidateCenter in replacementCandidates)
				{
					if (candidateCenter < 0 || candidateCenter >= centers.Count)
					{
						continue;
					}

					if (SamePixels(centers[candidateCenter], unique[worstUnique].Pixels))
					{
						continue;
					}

					var candidateCenters = new List<bool[]>(centers.Count);
					for (int c = 0; c < centers.Count; c++)
					{
						candidateCenters.Add(c == candidateCenter
							? CopyPixels(unique[worstUnique].Pixels)
							: centers[c]);
					}

					int[] candidateAssignments = CreateNearestAssignments(
						unique,
						candidateCenters,
						cancellationToken
					);
					ReductionErrorStats candidateStats = ComputeReductionErrorStats(
						unique,
						PackCenters(candidateCenters),
						candidateAssignments
					);
					if (candidateStats.FairnessCost + 1e-9 < best.FairnessCost)
					{
						best = candidateStats;
						bestCenter = candidateCenter;
						bestReplacement = candidateCenters[candidateCenter];
						bestAssignments = candidateAssignments;
					}
				}

				if (bestCenter < 0 || bestReplacement == null)
				{
					break;
				}

				centers[bestCenter] = bestReplacement;
				Array.Copy(bestAssignments, assignments, assignments.Length);
			}
		}

		private static int FindWorstRepresentedPatternIndex(
			List<GlyphPattern> unique,
			ulong[][] centerBits,
			int[] assignments
		)
		{
			int selected = 0;
			double worst = double.NegativeInfinity;
			for (int i = 0; i < unique.Count; i++)
			{
				int distance = HammingDistance(unique[i].PackedBits, centerBits[assignments[i]]);
				double score = (distance / (double)BitsPerGlyph)
					+ Math.Log(unique[i].Weight + 1.0) * 0.015;
				if (score > worst)
				{
					worst = score;
					selected = i;
				}
			}

			return selected;
		}

		private static int[] FindReplacementCandidates(
			List<GlyphPattern> unique,
			ulong[][] centerBits,
			int limit
		)
		{
			var removalCosts = new double[centerBits.Length];
			for (int i = 0; i < unique.Count; i++)
			{
				int best = int.MaxValue;
				int second = int.MaxValue;
				int bestCenter = 0;
				for (int c = 0; c < centerBits.Length; c++)
				{
					int distance = HammingDistance(unique[i].PackedBits, centerBits[c]);
					if (distance < best)
					{
						second = best;
						best = distance;
						bestCenter = c;
					}
					else if (distance < second)
					{
						second = distance;
					}
				}

				if (second == int.MaxValue)
				{
					second = best;
				}

				removalCosts[bestCenter] += (second - best) * unique[i].Weight;
			}

			var order = new int[centerBits.Length];
			for (int i = 0; i < order.Length; i++)
			{
				order[i] = i;
			}

			Array.Sort(order, (left, right) =>
			{
				int compare = removalCosts[left].CompareTo(removalCosts[right]);
				return compare != 0 ? compare : left.CompareTo(right);
			});

			if (order.Length <= limit)
			{
				return order;
			}

			var result = new int[limit];
			Array.Copy(order, result, result.Length);
			return result;
		}

		private static int[] CreateNearestAssignments(
			List<GlyphPattern> unique,
			List<bool[]> centers,
			CancellationToken cancellationToken
		)
		{
			var assignments = new int[unique.Count];
			Array.Fill(assignments, -1);
			AssignToNearestCenters(unique, centers, assignments, cancellationToken);
			return assignments;
		}

		private static ReductionErrorStats ComputeReductionErrorStats(
			List<GlyphPattern> unique,
			ulong[][] centerBits,
			int[] assignments
		)
		{
			var errors = new WeightedCellError[unique.Count];
			double totalError = 0.0;
			double totalWeight = 0.0;
			double worst = 0.0;
			for (int i = 0; i < unique.Count; i++)
			{
				int assignment = Math.Max(0, assignments[i]);
				int distance = HammingDistance(unique[i].PackedBits, centerBits[assignment]);
				double normalized = distance / (double)BitsPerGlyph;
				int weight = Math.Max(1, unique[i].Weight);
				errors[i] = new WeightedCellError(normalized, weight);
				totalError += normalized * weight;
				totalWeight += weight;
				if (normalized > worst)
				{
					worst = normalized;
				}
			}

			Array.Sort(errors, (left, right) => left.Error.CompareTo(right.Error));
			double high = WeightedQuantile(errors, totalWeight, 0.95);
			double average = totalWeight <= 0.0 ? 0.0 : totalError / totalWeight;
			return new ReductionErrorStats(average, high, worst);
		}

		private static double WeightedQuantile(
			WeightedCellError[] errors,
			double totalWeight,
			double quantile
		)
		{
			if (errors.Length == 0 || totalWeight <= 0.0)
			{
				return 0.0;
			}

			double target = totalWeight * Clamp01(quantile);
			double cumulative = 0.0;
			foreach (WeightedCellError error in errors)
			{
				cumulative += error.Weight;
				if (cumulative >= target)
				{
					return error.Error;
				}
			}

			return errors[errors.Length - 1].Error;
		}

		private static bool SamePixels(bool[] left, bool[] right)
		{
			if (left.Length != right.Length)
			{
				return false;
			}

			for (int i = 0; i < left.Length; i++)
			{
				if (left[i] != right[i])
				{
					return false;
				}
			}

			return true;
		}

		private static List<bool[]> InitializeCodebook(
			List<GlyphPattern> unique,
			int glyphCount
		)
		{
			var centers = new List<bool[]>(glyphCount);
			int first = 0;
			for (int i = 1; i < unique.Count; i++)
			{
				if (unique[i].Weight > unique[first].Weight)
				{
					first = i;
				}
			}

			centers.Add(CopyPixels(unique[first].Pixels));
			double[] nearestDistances = new double[unique.Count];
			for (int i = 0; i < nearestDistances.Length; i++)
			{
				nearestDistances[i] = double.PositiveInfinity;
			}

			while (centers.Count < glyphCount)
			{
				bool[] lastCenter = centers[centers.Count - 1];
				ulong[] lastCenterBits = PackBits(lastCenter);
				int selected = 0;
				double bestDistance = double.NegativeInfinity;
				for (int i = 0; i < unique.Count; i++)
				{
					int distance = HammingDistance(unique[i].PackedBits, lastCenterBits);
					if (distance < nearestDistances[i])
					{
						nearestDistances[i] = distance;
					}

					double weightedDistance = nearestDistances[i]
						* (1.0 + Math.Log(unique[i].Weight));
					if (weightedDistance > bestDistance)
					{
						bestDistance = weightedDistance;
						selected = i;
					}
				}

				centers.Add(CopyPixels(unique[selected].Pixels));
			}

			return centers;
		}

		private static bool AssignToNearestCenters(
			List<GlyphPattern> unique,
			List<bool[]> centers,
			int[] assignments,
			CancellationToken cancellationToken
		)
		{
			int changed = 0;
			ulong[][] centerBits = PackCenters(centers);
			Parallel.For(0, unique.Count, new ParallelOptions
			{
				CancellationToken = cancellationToken,
			}, i =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				int bestCenter = 0;
				int bestDistance = int.MaxValue;
				for (int c = 0; c < centers.Count; c++)
				{
					int distance = HammingDistance(unique[i].PackedBits, centerBits[c]);
					if (distance < bestDistance)
					{
						bestDistance = distance;
						bestCenter = c;
					}
				}

				if (assignments[i] != bestCenter)
				{
					assignments[i] = bestCenter;
					Interlocked.Exchange(ref changed, 1);
				}
			});

			return changed != 0;
		}

		private static void UpdateCenters(
			List<GlyphPattern> unique,
			List<bool[]> centers,
			int[] assignments
		)
		{
			int[][] bitCounts = new int[centers.Count][];
			int[] weights = new int[centers.Count];
			for (int c = 0; c < centers.Count; c++)
			{
				bitCounts[c] = new int[BitsPerGlyph];
			}

			for (int i = 0; i < unique.Count; i++)
			{
				int center = assignments[i];
				int weight = unique[i].Weight;
				weights[center] += weight;
				for (int bit = 0; bit < BitsPerGlyph; bit++)
				{
					if (unique[i].Pixels[bit])
					{
						bitCounts[center][bit] += weight;
					}
				}
			}

			var usedKeys = new HashSet<string>(StringComparer.Ordinal);
			for (int c = 0; c < centers.Count; c++)
			{
				if (weights[c] == 0)
				{
					centers[c] = CopyPixels(FindWorstRepresentedPattern(unique, centers));
				}
				else
				{
					bool[] next = new bool[BitsPerGlyph];
					for (int bit = 0; bit < BitsPerGlyph; bit++)
					{
						if (bitCounts[c][bit] * 2 > weights[c])
						{
							next[bit] = true;
						}
						else if (bitCounts[c][bit] * 2 == weights[c])
						{
							next[bit] = centers[c][bit];
						}
					}

					centers[c] = next;
				}

				string key = EncodeSixelPattern(centers[c]);
				if (!usedKeys.Add(key))
				{
					centers[c] = CopyPixels(FindWorstRepresentedPattern(unique, centers));
					usedKeys.Add(EncodeSixelPattern(centers[c]));
				}
			}
		}

		private static bool[] FindWorstRepresentedPattern(
			List<GlyphPattern> unique,
			List<bool[]> centers
		)
		{
			int selected = 0;
			double bestDistance = double.NegativeInfinity;
			ulong[][] centerBits = PackCenters(centers);
			for (int i = 0; i < unique.Count; i++)
			{
				int nearest = int.MaxValue;
				for (int c = 0; c < centers.Count; c++)
				{
					nearest = Math.Min(
						nearest,
						HammingDistance(unique[i].PackedBits, centerBits[c])
					);
				}

				double weighted = nearest * (1.0 + Math.Log(unique[i].Weight));
				if (weighted > bestDistance)
				{
					bestDistance = weighted;
					selected = i;
				}
			}

			return unique[selected].Pixels;
		}

		private static bool[] ReconstructBitmap(
			PackedScreen packed,
			int cellsX,
			int cellsY
		)
		{
			int width = cellsX * Gif320RenderOptions.CellPixelWidth;
			int height = cellsY * Gif320RenderOptions.CellPixelHeight;
			var bitmap = new bool[width * height];

			for (int cellY = 0; cellY < cellsY; cellY++)
			{
				for (int cellX = 0; cellX < cellsX; cellX++)
				{
					int cellIndex = cellY * cellsX + cellX;
					int glyphIndex = packed.CellGlyphIndexes[cellIndex];
					bool[]? glyph = glyphIndex >= 0 ? packed.Glyphs[glyphIndex].Pixels : null;
					bool reverseVideo = packed.CellReverseVideo[cellIndex];
					for (int y = 0; y < Gif320RenderOptions.CellPixelHeight; y++)
					{
						int targetOffset = (cellY * Gif320RenderOptions.CellPixelHeight + y)
							* width
							+ cellX * Gif320RenderOptions.CellPixelWidth;
						int sourceOffset = y * Gif320RenderOptions.CellPixelWidth;
						for (int x = 0; x < Gif320RenderOptions.CellPixelWidth; x++)
						{
							bool pixel = glyph != null && glyph[sourceOffset + x];
							bitmap[targetOffset + x] = reverseVideo ? !pixel : pixel;
						}
					}
				}
			}

			return bitmap;
		}

		private static string[] BuildScreenRows(
			PackedScreen packed,
			int cellsX,
			int cellsY
		)
		{
			var rows = new string[cellsY];
			for (int y = 0; y < cellsY; y++)
			{
				var chars = new char[cellsX];
				for (int x = 0; x < cellsX; x++)
				{
					int glyphIndex = packed.CellGlyphIndexes[y * cellsX + x];
					chars[x] = glyphIndex >= 0 ? (char)('!' + glyphIndex) : ' ';
				}

				rows[y] = new string(chars);
			}

			return rows;
		}

		private static string BuildVtSequence(
			PackedScreen packed,
			string[] rows,
			Gif320RenderOptions options
		)
		{
			var builder = new StringBuilder();
			if (options.IncludeTerminalSetup)
			{
				builder.Append("\u001b[63;1\"p");
				builder.Append("\u001b[24*|");
				builder.Append("\u001b[?3l");
				builder.Append("\u001b[?5l");
				builder.Append("\u001b[H");
				builder.Append("\u001b[J");
			}

			builder.Append("\u001bP1;1;0;15;1;2;12;0{ @");
			for (int i = 0; i < packed.Glyphs.Count; i++)
			{
				if (i > 0)
				{
					builder.Append(';');
				}

				builder.Append(packed.Glyphs[i].SixelPattern);
			}

			builder.Append("\u001b\\");
			builder.Append("\u001b) @");
			builder.Append('\u000e');
			builder.Append("\u001b[1m");

			int terminalWidth = options.DoubleSize ? options.CellsX * 2 : options.CellsX;
			int terminalHeight = options.DoubleSize ? options.CellsY * 2 : options.CellsY;
			int startColumn = options.CenterOnScreen
				? Math.Max(1, ((Gif320RenderOptions.TerminalColumns - terminalWidth) / 2) + 1)
				: options.StartColumn;
			int startRow = options.CenterOnScreen
				? Math.Max(1, ((Gif320RenderOptions.TerminalRows - terminalHeight) / 2) + 1)
				: options.StartRow;

			for (int y = 0; y < rows.Length; y++)
			{
				if (options.DoubleSize)
				{
					AppendCursorMove(builder, startRow + y * 2, startColumn);
					builder.Append("\u001b#3");
					AppendScreenRow(builder, rows[y], packed.CellReverseVideo, y, options.CellsX);
					AppendCursorMove(builder, startRow + y * 2 + 1, startColumn);
					builder.Append("\u001b#4");
					AppendScreenRow(builder, rows[y], packed.CellReverseVideo, y, options.CellsX);
				}
				else
				{
					AppendCursorMove(builder, startRow + y, startColumn);
					builder.Append("\u001b#5");
					AppendScreenRow(builder, rows[y], packed.CellReverseVideo, y, options.CellsX);
				}
			}

			if (options.IncludeTerminalReset)
			{
				builder.Append('\u000f');
				builder.Append("\u001b[27m");
				builder.Append("\u001b[22m");
				builder.Append("\u001b(B");
			}

			return builder.ToString();
		}

		private static void AppendScreenRow(
			StringBuilder builder,
			string row,
			bool[] reverseVideoCells,
			int rowIndex,
			int cellsX
		)
		{
			bool reverseVideo = false;
			int offset = rowIndex * cellsX;
			for (int x = 0; x < row.Length; x++)
			{
				bool nextReverseVideo = reverseVideoCells[offset + x];
				if (nextReverseVideo != reverseVideo)
				{
					builder.Append(nextReverseVideo ? "\u001b[7m" : "\u001b[27m");
					reverseVideo = nextReverseVideo;
				}

				builder.Append(row[x]);
			}

			if (reverseVideo)
			{
				builder.Append("\u001b[27m");
			}
		}

		private static void AppendCursorMove(
			StringBuilder builder,
			int row,
			int column
		)
		{
			builder.Append("\u001b[");
			builder.Append(row.ToString(CultureInfo.InvariantCulture));
			builder.Append(';');
			builder.Append(column.ToString(CultureInfo.InvariantCulture));
			builder.Append('f');
		}

		private static LinearImage ResizeToLinearImage(
			ReadOnlySpan<byte> source,
			int sourceWidth,
			int sourceHeight,
			Gif320PixelFormat pixelFormat,
			Gif320RenderOptions options
		)
		{
			int targetWidth = options.CellsX * Gif320RenderOptions.CellPixelWidth;
			int targetHeight = options.CellsY * Gif320RenderOptions.CellPixelHeight;
			double displayTargetHeight =
				targetHeight * Gif320RenderOptions.DisplayPixelHeightScale;
			var red = new double[targetWidth * targetHeight];
			var green = new double[red.Length];
			var blue = new double[red.Length];

			double scaleX = (double)targetWidth / sourceWidth;
			double scaleY = displayTargetHeight / sourceHeight;
			double scale;
			switch (options.ResizeMode)
			{
				case Gif320ResizeMode.Stretch:
					scale = 0.0;
					break;
				case Gif320ResizeMode.Contain:
					scale = Math.Min(scaleX, scaleY);
					break;
				case Gif320ResizeMode.Cover:
					scale = Math.Max(scaleX, scaleY);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(options));
			}

			double scaledWidth = options.ResizeMode == Gif320ResizeMode.Stretch
				? targetWidth
				: sourceWidth * scale;
			double scaledHeight = options.ResizeMode == Gif320ResizeMode.Stretch
				? displayTargetHeight
				: sourceHeight * scale;
			double offsetX = (targetWidth - scaledWidth) * 0.5;
			double offsetY = (displayTargetHeight - scaledHeight) * 0.5;

			for (int y = 0; y < targetHeight; y++)
			{
				for (int x = 0; x < targetWidth; x++)
				{
					double sourceX;
					double sourceY;
					if (options.ResizeMode == Gif320ResizeMode.Stretch)
					{
						sourceX = ((x + 0.5) * sourceWidth / targetWidth) - 0.5;
						sourceY = ((y + 0.5) * sourceHeight / targetHeight) - 0.5;
					}
					else
					{
						double displayY =
							(y + 0.5) * Gif320RenderOptions.DisplayPixelHeightScale;
						sourceX = ((x + 0.5 - offsetX) / scale) - 0.5;
						sourceY = ((displayY - offsetY) / scale) - 0.5;
					}

					int index = y * targetWidth + x;
					if (sourceX < 0.0
						|| sourceY < 0.0
						|| sourceX > sourceWidth - 1
						|| sourceY > sourceHeight - 1)
					{
						continue;
					}

					SampleBilinear(
						source,
						sourceWidth,
						sourceHeight,
						pixelFormat,
						sourceX,
						sourceY,
						out double r,
						out double g,
						out double b
					);
					red[index] = r;
					green[index] = g;
					blue[index] = b;
				}
			}

			return new LinearImage(targetWidth, targetHeight, red, green, blue);
		}

		private static LinearImage CreateAutoTuneSearchImage(LinearImage image)
		{
			int pixels = image.Width * image.Height;
			if (pixels <= MaxAutoTuneSearchPixels)
			{
				return image;
			}

			double scale = Math.Sqrt(MaxAutoTuneSearchPixels / (double)pixels);
			int targetWidth = Math.Max(3, (int)Math.Round(image.Width * scale));
			int targetHeight = Math.Max(3, (int)Math.Round(image.Height * scale));
			return ResizeLinearImage(image, targetWidth, targetHeight);
		}

		private static LinearImage ResizeLinearImage(
			LinearImage source,
			int targetWidth,
			int targetHeight
		)
		{
			var red = new double[targetWidth * targetHeight];
			var green = new double[red.Length];
			var blue = new double[red.Length];
			for (int y = 0; y < targetHeight; y++)
			{
				double sourceY = ((y + 0.5) * source.Height / targetHeight) - 0.5;
				for (int x = 0; x < targetWidth; x++)
				{
					double sourceX = ((x + 0.5) * source.Width / targetWidth) - 0.5;
					int index = y * targetWidth + x;
					SampleLinearBilinear(
						source,
						sourceX,
						sourceY,
						out red[index],
						out green[index],
						out blue[index]
					);
				}
			}

			return new LinearImage(targetWidth, targetHeight, red, green, blue);
		}

		private static void SampleLinearBilinear(
			LinearImage source,
			double x,
			double y,
			out double red,
			out double green,
			out double blue
		)
		{
			int x0 = ClampInt((int)Math.Floor(x), 0, source.Width - 1);
			int y0 = ClampInt((int)Math.Floor(y), 0, source.Height - 1);
			int x1 = ClampInt(x0 + 1, 0, source.Width - 1);
			int y1 = ClampInt(y0 + 1, 0, source.Height - 1);
			double fx = x - x0;
			double fy = y - y0;
			int offset00 = y0 * source.Width + x0;
			int offset10 = y0 * source.Width + x1;
			int offset01 = y1 * source.Width + x0;
			int offset11 = y1 * source.Width + x1;
			red = Bilinear(
				source.Red[offset00],
				source.Red[offset10],
				source.Red[offset01],
				source.Red[offset11],
				fx,
				fy
			);
			green = Bilinear(
				source.Green[offset00],
				source.Green[offset10],
				source.Green[offset01],
				source.Green[offset11],
				fx,
				fy
			);
			blue = Bilinear(
				source.Blue[offset00],
				source.Blue[offset10],
				source.Blue[offset01],
				source.Blue[offset11],
				fx,
				fy
			);
		}

		private static void SampleBilinear(
			ReadOnlySpan<byte> source,
			int width,
			int height,
			Gif320PixelFormat format,
			double x,
			double y,
			out double red,
			out double green,
			out double blue
		)
		{
			int x0 = ClampInt((int)Math.Floor(x), 0, width - 1);
			int y0 = ClampInt((int)Math.Floor(y), 0, height - 1);
			int x1 = ClampInt(x0 + 1, 0, width - 1);
			int y1 = ClampInt(y0 + 1, 0, height - 1);
			double fx = x - x0;
			double fy = y - y0;

			ReadPixel(source, width, format, x0, y0, out double r00, out double g00, out double b00);
			ReadPixel(source, width, format, x1, y0, out double r10, out double g10, out double b10);
			ReadPixel(source, width, format, x0, y1, out double r01, out double g01, out double b01);
			ReadPixel(source, width, format, x1, y1, out double r11, out double g11, out double b11);

			red = Bilinear(r00, r10, r01, r11, fx, fy);
			green = Bilinear(g00, g10, g01, g11, fx, fy);
			blue = Bilinear(b00, b10, b01, b11, fx, fy);
		}

		private static void ReadPixel(
			ReadOnlySpan<byte> source,
			int width,
			Gif320PixelFormat format,
			int x,
			int y,
			out double red,
			out double green,
			out double blue
		)
		{
			int bytesPerPixel = BytesPerPixel(format);
			int offset = ((y * width) + x) * bytesPerPixel;
			byte r;
			byte g;
			byte b;
			byte a = 255;

			switch (format)
			{
				case Gif320PixelFormat.Rgb24:
					r = source[offset];
					g = source[offset + 1];
					b = source[offset + 2];
					break;
				case Gif320PixelFormat.Rgba32:
					r = source[offset];
					g = source[offset + 1];
					b = source[offset + 2];
					a = source[offset + 3];
					break;
				case Gif320PixelFormat.Bgra32:
					b = source[offset];
					g = source[offset + 1];
					r = source[offset + 2];
					a = source[offset + 3];
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(format));
			}

			double alpha = a / 255.0;
			red = SrgbToLinear[r] * alpha;
			green = SrgbToLinear[g] * alpha;
			blue = SrgbToLinear[b] * alpha;
		}

		private static PackedScreen BuildExactPackedScreen(
			List<GlyphPattern> glyphs,
			int[] cellGlyphIndexes,
			int uniqueGlyphCount,
			double reductionErrorPerCellPixel
		)
		{
			return new PackedScreen(
				glyphs,
				cellGlyphIndexes,
				new bool[cellGlyphIndexes.Length],
				uniqueGlyphCount,
				reductionErrorPerCellPixel,
				reductionErrorPerCellPixel,
				reductionErrorPerCellPixel
			);
		}

		private static bool[] ExtractCell(
			bool[] bitmap,
			int bitmapWidth,
			int cellX,
			int cellY
		)
		{
			var pixels = new bool[BitsPerGlyph];
			int sourceX = cellX * Gif320RenderOptions.CellPixelWidth;
			int sourceY = cellY * Gif320RenderOptions.CellPixelHeight;
			for (int y = 0; y < Gif320RenderOptions.CellPixelHeight; y++)
			{
				int sourceOffset = (sourceY + y) * bitmapWidth + sourceX;
				int targetOffset = y * Gif320RenderOptions.CellPixelWidth;
				for (int x = 0; x < Gif320RenderOptions.CellPixelWidth; x++)
				{
					pixels[targetOffset + x] = bitmap[sourceOffset + x];
				}
			}

			return pixels;
		}

		private static string EncodeSixelPattern(bool[] pixels)
		{
			var builder = new StringBuilder(31);
			AppendSixelRow(builder, pixels, yOffset: 0);
			builder.Append('/');
			AppendSixelRow(builder, pixels, yOffset: 6);
			return builder.ToString();
		}

		private static void AppendSixelRow(
			StringBuilder builder,
			bool[] pixels,
			int yOffset
		)
		{
			for (int x = 0; x < Gif320RenderOptions.CellPixelWidth; x++)
			{
				int value = 0;
				for (int y = 0; y < 6; y++)
				{
					int pixelIndex = (yOffset + y) * Gif320RenderOptions.CellPixelWidth + x;
					if (pixels[pixelIndex])
					{
						value |= 1 << y;
					}
				}

				builder.Append((char)(value + 0x3f));
			}
		}

		private static IReadOnlyList<string> GetSixelPatterns(
			IReadOnlyList<GlyphPattern> glyphs
		)
		{
			var patterns = new string[glyphs.Count];
			for (int i = 0; i < glyphs.Count; i++)
			{
				patterns[i] = glyphs[i].SixelPattern;
			}

			return patterns;
		}

		private static string BuildCellMap(PackedScreen packed, int cellsX, int cellsY)
		{
			var builder = new StringBuilder(CellMapPrefix);
			builder.Append(cellsX.ToString(CultureInfo.InvariantCulture));
			builder.Append('x');
			builder.Append(cellsY.ToString(CultureInfo.InvariantCulture));
			builder.Append(':');
			for (int i = 0; i < packed.CellGlyphIndexes.Length; i++)
			{
				int glyphIndex = packed.CellGlyphIndexes[i];
				int code = glyphIndex >= 0 ? glyphIndex + 1 : 0;
				if (packed.CellReverseVideo[i])
				{
					code |= 0x80;
				}

				builder.Append(code.ToString("x2", CultureInfo.InvariantCulture));
			}

			return builder.ToString();
		}

		private static string BuildGlyphAtlas(IReadOnlyList<GlyphPattern> glyphs)
		{
			var builder = new StringBuilder(AtlasPrefix);
			for (int glyphIndex = 0; glyphIndex < glyphs.Count; glyphIndex++)
			{
				if (glyphIndex > 0)
				{
					builder.Append(',');
				}

				AppendGlyphHex(builder, glyphs[glyphIndex].Pixels);
			}

			return builder.ToString();
		}

		private static void ApplyManualAtlas(PackedScreen packed, string? manualAtlas)
		{
			List<bool[]> overrides = ParseManualAtlas(manualAtlas);
			int count = Math.Min(overrides.Count, packed.Glyphs.Count);
			for (int i = 0; i < count; i++)
			{
				bool[] pixels = overrides[i];
				packed.Glyphs[i] = new GlyphPattern(pixels, PackBits(pixels));
			}
		}

		private static void ApplyManualCellMap(
			PackedScreen packed,
			string? manualAtlas,
			string? manualCellMap,
			int cellsX,
			int cellsY
		)
		{
			List<bool[]> overrides = ParseManualAtlas(manualAtlas);
			byte[] map = ParseManualCellMap(manualCellMap, cellsX, cellsY);
			if (overrides.Count == 0 || map.Length == 0)
			{
				return;
			}

			packed.Glyphs.Clear();
			foreach (bool[] pixels in overrides)
			{
				packed.Glyphs.Add(new GlyphPattern(pixels, PackBits(pixels)));
			}

			for (int i = 0; i < map.Length && i < packed.CellGlyphIndexes.Length; i++)
			{
				int code = map[i] & 0x7f;
				int glyphIndex = code == 0 ? -1 : code - 1;
				packed.CellGlyphIndexes[i] = glyphIndex >= 0 && glyphIndex < packed.Glyphs.Count
					? glyphIndex
					: -1;
				packed.CellReverseVideo[i] = (map[i] & 0x80) != 0;
			}
		}

		private static List<bool[]> ParseManualAtlas(string? manualAtlas)
		{
			var glyphs = new List<bool[]>();
			if (string.IsNullOrWhiteSpace(manualAtlas))
			{
				return glyphs;
			}

			string text = manualAtlas.Trim();
			if (text.StartsWith(AtlasPrefix, StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(AtlasPrefix.Length);
			}

			var token = new StringBuilder(BytesPerGlyph * 2);
			foreach (char c in text)
			{
				if (IsHexDigit(c))
				{
					token.Append(c);
				}
				else if (c == ',' || c == ';' || char.IsWhiteSpace(c))
				{
					FlushAtlasToken(token, glyphs);
				}
				else
				{
					throw new ArgumentException(
						"Manual atlas contains an unsupported character.",
						nameof(manualAtlas)
					);
				}
			}

			FlushAtlasToken(token, glyphs);
			return glyphs;
		}

		private static byte[] ParseManualCellMap(string? manualCellMap, int cellsX, int cellsY)
		{
			if (string.IsNullOrWhiteSpace(manualCellMap))
			{
				return Array.Empty<byte>();
			}

			string text = manualCellMap.Trim();
			if (!text.StartsWith(CellMapPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return Array.Empty<byte>();
			}

			int dimensionsStart = CellMapPrefix.Length;
			int xIndex = text.IndexOf('x', dimensionsStart);
			if (xIndex < 0)
			{
				return Array.Empty<byte>();
			}

			int separatorIndex = text.IndexOf(':', xIndex + 1);
			if (separatorIndex < 0
				|| !int.TryParse(
					text.AsSpan(dimensionsStart, xIndex - dimensionsStart),
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out int parsedCellsX
				)
				|| !int.TryParse(
					text.AsSpan(xIndex + 1, separatorIndex - xIndex - 1),
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out int parsedCellsY
				)
				|| parsedCellsX != cellsX
				|| parsedCellsY != cellsY)
			{
				return Array.Empty<byte>();
			}

			string hex = text.Substring(separatorIndex + 1);
			int cellCount = checked(cellsX * cellsY);
			if (hex.Length != cellCount * 2)
			{
				return Array.Empty<byte>();
			}

			var map = new byte[cellCount];
			for (int i = 0; i < map.Length; i++)
			{
				char high = hex[i * 2];
				char low = hex[i * 2 + 1];
				if (!IsHexDigit(high) || !IsHexDigit(low))
				{
					return Array.Empty<byte>();
				}

				map[i] = (byte)((HexValue(high) << 4) | HexValue(low));
			}

			return map;
		}

		private static void FlushAtlasToken(StringBuilder token, List<bool[]> glyphs)
		{
			if (token.Length == 0)
			{
				return;
			}

			if (token.Length != BytesPerGlyph * 2)
			{
				throw new ArgumentException(
					$"Manual atlas glyphs must be {BytesPerGlyph * 2} hexadecimal characters."
				);
			}

			if (glyphs.Count >= 94)
			{
				throw new ArgumentException("Manual atlas cannot contain more than 94 glyphs.");
			}

			var pixels = new bool[BitsPerGlyph];
			for (int byteIndex = 0; byteIndex < BytesPerGlyph; byteIndex++)
			{
				byte value = (byte)((HexValue(token[byteIndex * 2]) << 4)
					| HexValue(token[byteIndex * 2 + 1]));
				for (int bit = 0; bit < 8; bit++)
				{
					int pixel = byteIndex * 8 + bit;
					if (pixel < pixels.Length)
					{
						pixels[pixel] = (value & (1 << bit)) != 0;
					}
				}
			}

			glyphs.Add(pixels);
			token.Clear();
		}

		private static void AppendGlyphHex(StringBuilder builder, bool[] pixels)
		{
			for (int byteIndex = 0; byteIndex < BytesPerGlyph; byteIndex++)
			{
				int value = 0;
				for (int bit = 0; bit < 8; bit++)
				{
					int pixel = byteIndex * 8 + bit;
					if (pixel < pixels.Length && pixels[pixel])
					{
						value |= 1 << bit;
					}
				}

				builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
			}
		}

		private static bool IsHexDigit(char value)
		{
			return (value >= '0' && value <= '9')
				|| (value >= 'a' && value <= 'f')
				|| (value >= 'A' && value <= 'F');
		}

		private static int HexValue(char value)
		{
			if (value >= '0' && value <= '9')
			{
				return value - '0';
			}

			if (value >= 'a' && value <= 'f')
			{
				return value - 'a' + 10;
			}

			if (value >= 'A' && value <= 'F')
			{
				return value - 'A' + 10;
			}

			throw new ArgumentException("Invalid hexadecimal digit.");
		}

		private static double ScoreImage(
			double[] reference,
			bool[] output,
			int width,
			int height,
			double reductionErrorPerCellPixel,
			double reductionHighErrorPerCellPixel,
			double reductionWorstErrorPerCellPixel,
			double glyphPressurePenalty,
			Gif320RenderOptions options
		)
		{
			double[] binary = new double[output.Length];
			for (int i = 0; i < output.Length; i++)
			{
				binary[i] = output[i] ? 1.0 : 0.0;
			}

			double[] blurredOutput = BoxBlur(binary, width, height);
			double ssim = StructuralSimilarity(reference, blurredOutput);
			double edge = EdgeCorrelation(reference, binary, width, height);
			double toneScore = 1.0 - Math.Min(1.0, RootMeanSquareError(reference, blurredOutput) * 1.25);
			double pixelScore = 1.0 - Math.Min(1.0, RootMeanSquareError(reference, binary) * 1.10);
			double cellFitScore = WorstCellFitScore(reference, binary, width, height);
			double smoothScore = SmoothnessScore(output, width, height);
			double reductionScore = ReductionFitScore(
				reductionErrorPerCellPixel,
				reductionHighErrorPerCellPixel,
				reductionWorstErrorPerCellPixel
			);

			double frequencyBias = options.AutoTuneFrequencyPreference / 100.0;
			double smoothnessBias = options.AutoTuneSmoothnessPreference / 100.0;
			double glyphReuseBias = options.AutoTuneGlyphReusePreference / 100.0;

			double ssimWeight = Math.Max(0.0, 0.50 - 0.15 * frequencyBias + 0.05 * smoothnessBias);
			double edgeWeight = Math.Max(0.0, 0.25 + 0.20 * frequencyBias - 0.10 * smoothnessBias);
			double toneWeight = Math.Max(0.0, 0.20 - 0.10 * frequencyBias + 0.05 * smoothnessBias);
			double pixelWeight = Math.Max(0.0, 0.10 * frequencyBias - 0.05 * smoothnessBias);
			double cellFitWeight = Math.Max(0.0, 0.16 + 0.08 * glyphReuseBias);
			double smoothWeight = Math.Max(0.0, 0.12 * smoothnessBias);
			double reductionWeight = Math.Max(0.0, 0.05 + 0.10 * glyphReuseBias);
			double totalWeight = ssimWeight
				+ edgeWeight
				+ toneWeight
				+ pixelWeight
				+ cellFitWeight
				+ smoothWeight
				+ reductionWeight;
			if (totalWeight <= 0.0)
			{
				totalWeight = 1.0;
			}

			double weightedScore = (ssimWeight * ssim)
				+ (edgeWeight * edge)
				+ (toneWeight * toneScore)
				+ (pixelWeight * pixelScore)
				+ (cellFitWeight * cellFitScore)
				+ (smoothWeight * smoothScore)
				+ (reductionWeight * reductionScore);
			double glyphPressureMultiplier = Math.Max(0.0, 1.0 + glyphReuseBias);
			double score = (weightedScore / totalWeight)
				- glyphPressurePenalty * glyphPressureMultiplier;
			return Clamp01(score);
		}

		private static double WorstCellFitScore(
			double[] reference,
			double[] output,
			int width,
			int height
		)
		{
			int cellsX = Math.Max(1, width / Gif320RenderOptions.CellPixelWidth);
			int cellsY = Math.Max(1, height / Gif320RenderOptions.CellPixelHeight);
			var errors = new double[cellsX * cellsY];
			int index = 0;
			for (int cellY = 0; cellY < cellsY; cellY++)
			{
				int yStart = cellY * Gif320RenderOptions.CellPixelHeight;
				int yEnd = Math.Min(height, yStart + Gif320RenderOptions.CellPixelHeight);
				for (int cellX = 0; cellX < cellsX; cellX++)
				{
					int xStart = cellX * Gif320RenderOptions.CellPixelWidth;
					int xEnd = Math.Min(width, xStart + Gif320RenderOptions.CellPixelWidth);
					double sum = 0.0;
					int count = 0;
					for (int y = yStart; y < yEnd; y++)
					{
						int rowOffset = y * width;
						for (int x = xStart; x < xEnd; x++)
						{
							double delta = reference[rowOffset + x] - output[rowOffset + x];
							sum += delta * delta;
							count++;
						}
					}

					errors[index++] = count == 0 ? 0.0 : Math.Sqrt(sum / count);
				}
			}

			Array.Sort(errors);
			double p90 = errors[(int)Math.Floor((errors.Length - 1) * 0.90)];
			double p98 = errors[(int)Math.Floor((errors.Length - 1) * 0.98)];
			double worst = errors[errors.Length - 1];
			double badness = (p90 * p90 * 0.25)
				+ (p98 * p98 * 0.35)
				+ (worst * worst * 0.75)
				+ (worst * worst * worst * 0.35);
			return 1.0 - Math.Min(1.0, badness);
		}

		private static double ReductionFitScore(
			double average,
			double high,
			double worst
		)
		{
			double badness = Clamp01(average) / 0.45 * 0.30
				+ Clamp01(high) / 0.55 * 0.30
				+ Math.Pow(Clamp01(worst) / 0.65, 1.7) * 0.40;
			return 1.0 - Math.Min(1.0, badness);
		}

		private static double SmoothnessScore(bool[] output, int width, int height)
		{
			if (width < 3 || height < 3)
			{
				return 1.0;
			}

			int isolated = 0;
			int sampled = 0;
			for (int y = 1; y < height - 1; y++)
			{
				for (int x = 1; x < width - 1; x++)
				{
					bool center = output[y * width + x];
					int matchingNeighbors = 0;
					for (int yy = -1; yy <= 1; yy++)
					{
						for (int xx = -1; xx <= 1; xx++)
						{
							if (xx == 0 && yy == 0)
							{
								continue;
							}

							if (output[(y + yy) * width + x + xx] == center)
							{
								matchingNeighbors++;
							}
						}
					}

					if (matchingNeighbors <= 2)
					{
						isolated++;
					}

					sampled++;
				}
			}

			if (sampled == 0)
			{
				return 1.0;
			}

			return 1.0 - Math.Min(1.0, isolated / (double)sampled * 4.0);
		}

		private static double StructuralSimilarity(double[] left, double[] right)
		{
			double meanLeft = 0.0;
			double meanRight = 0.0;
			for (int i = 0; i < left.Length; i++)
			{
				meanLeft += left[i];
				meanRight += right[i];
			}

			meanLeft /= left.Length;
			meanRight /= right.Length;

			double varLeft = 0.0;
			double varRight = 0.0;
			double covariance = 0.0;
			for (int i = 0; i < left.Length; i++)
			{
				double leftDelta = left[i] - meanLeft;
				double rightDelta = right[i] - meanRight;
				varLeft += leftDelta * leftDelta;
				varRight += rightDelta * rightDelta;
				covariance += leftDelta * rightDelta;
			}

			double denom = Math.Max(1, left.Length - 1);
			varLeft /= denom;
			varRight /= denom;
			covariance /= denom;

			const double c1 = 0.01 * 0.01;
			const double c2 = 0.03 * 0.03;
			double numerator = (2.0 * meanLeft * meanRight + c1)
				* (2.0 * covariance + c2);
			double denominator = (meanLeft * meanLeft + meanRight * meanRight + c1)
				* (varLeft + varRight + c2);
			if (denominator <= 0.0)
			{
				return 0.0;
			}

			return Clamp01((numerator / denominator + 1.0) * 0.5);
		}

		private static double EdgeCorrelation(
			double[] reference,
			double[] output,
			int width,
			int height
		)
		{
			double dot = 0.0;
			double referenceNorm = 0.0;
			double outputNorm = 0.0;
			for (int y = 1; y < height - 1; y++)
			{
				for (int x = 1; x < width - 1; x++)
				{
					double refEdge = SobelMagnitude(reference, width, x, y);
					double outEdge = SobelMagnitude(output, width, x, y);
					dot += refEdge * outEdge;
					referenceNorm += refEdge * refEdge;
					outputNorm += outEdge * outEdge;
				}
			}

			double denominator = Math.Sqrt(referenceNorm * outputNorm);
			return denominator <= 1e-12 ? 0.0 : Clamp01(dot / denominator);
		}

		private static double SobelMagnitude(
			double[] values,
			int width,
			int x,
			int y
		)
		{
			int offset = y * width + x;
			double gx =
				-values[offset - width - 1]
				+ values[offset - width + 1]
				- 2.0 * values[offset - 1]
				+ 2.0 * values[offset + 1]
				- values[offset + width - 1]
				+ values[offset + width + 1];
			double gy =
				-values[offset - width - 1]
				- 2.0 * values[offset - width]
				- values[offset - width + 1]
				+ values[offset + width - 1]
				+ 2.0 * values[offset + width]
				+ values[offset + width + 1];
			return Math.Sqrt(gx * gx + gy * gy);
		}

		private static double[] BoxBlur(double[] values, int width, int height)
		{
			var output = new double[values.Length];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					double sum = 0.0;
					int count = 0;
					for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
					{
						for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
						{
							sum += values[yy * width + xx];
							count++;
						}
					}

					output[y * width + x] = sum / count;
				}
			}

			return output;
		}

		private static double RootMeanSquareError(double[] left, double[] right)
		{
			double sum = 0.0;
			for (int i = 0; i < left.Length; i++)
			{
				double delta = left[i] - right[i];
				sum += delta * delta;
			}

			return Math.Sqrt(sum / left.Length);
		}

		private static double[] ApplyClahe(
			double[] values,
			int width,
			int height,
			int tilesX,
			int tilesY,
			double clipLimit
		)
		{
			const int bins = 128;
			var maps = new double[tilesX * tilesY][];
			for (int tileY = 0; tileY < tilesY; tileY++)
			{
				for (int tileX = 0; tileX < tilesX; tileX++)
				{
					int left = tileX * width / tilesX;
					int right = (tileX + 1) * width / tilesX;
					int top = tileY * height / tilesY;
					int bottom = (tileY + 1) * height / tilesY;
					maps[tileY * tilesX + tileX] = BuildClippedCdf(
						values,
						width,
						left,
						top,
						right,
						bottom,
						bins,
						clipLimit
					);
				}
			}

			var output = new double[values.Length];
			for (int y = 0; y < height; y++)
			{
				double tilePositionY = ((y + 0.5) * tilesY / height) - 0.5;
				int y0 = ClampInt((int)Math.Floor(tilePositionY), 0, tilesY - 1);
				int y1 = ClampInt(y0 + 1, 0, tilesY - 1);
				double fy = Clamp01(tilePositionY - y0);

				for (int x = 0; x < width; x++)
				{
					double tilePositionX = ((x + 0.5) * tilesX / width) - 0.5;
					int x0 = ClampInt((int)Math.Floor(tilePositionX), 0, tilesX - 1);
					int x1 = ClampInt(x0 + 1, 0, tilesX - 1);
					double fx = Clamp01(tilePositionX - x0);

					double value = values[y * width + x];
					double mapped00 = MapByCdf(maps[y0 * tilesX + x0], value, bins);
					double mapped10 = MapByCdf(maps[y0 * tilesX + x1], value, bins);
					double mapped01 = MapByCdf(maps[y1 * tilesX + x0], value, bins);
					double mapped11 = MapByCdf(maps[y1 * tilesX + x1], value, bins);
					output[y * width + x] = Bilinear(
						mapped00,
						mapped10,
						mapped01,
						mapped11,
						fx,
						fy
					);
				}
			}

			return output;
		}

		private static double[] BuildClippedCdf(
			double[] values,
			int width,
			int left,
			int top,
			int right,
			int bottom,
			int bins,
			double clipLimit
		)
		{
			var histogram = new int[bins];
			for (int y = top; y < bottom; y++)
			{
				for (int x = left; x < right; x++)
				{
					int bin = ClampInt((int)(values[y * width + x] * (bins - 1)), 0, bins - 1);
					histogram[bin]++;
				}
			}

			int pixels = Math.Max(1, (right - left) * (bottom - top));
			int limit = Math.Max(1, (int)Math.Round(clipLimit * pixels));
			int excess = 0;
			for (int i = 0; i < histogram.Length; i++)
			{
				if (histogram[i] > limit)
				{
					excess += histogram[i] - limit;
					histogram[i] = limit;
				}
			}

			int redistributed = excess / bins;
			int remainder = excess % bins;
			for (int i = 0; i < histogram.Length; i++)
			{
				histogram[i] += redistributed;
				if (i < remainder)
				{
					histogram[i]++;
				}
			}

			var cdf = new double[bins];
			int cumulative = 0;
			for (int i = 0; i < bins; i++)
			{
				cumulative += histogram[i];
				cdf[i] = cumulative / (double)pixels;
			}

			return cdf;
		}

		private static double MapByCdf(double[] cdf, double value, int bins)
		{
			int bin = ClampInt((int)(Clamp01(value) * (bins - 1)), 0, bins - 1);
			return Clamp01(cdf[bin]);
		}

		private static double OtsuThreshold(double[] values)
		{
			const int bins = 256;
			var histogram = new int[bins];
			for (int i = 0; i < values.Length; i++)
			{
				int bin = ClampInt((int)(Clamp01(values[i]) * (bins - 1)), 0, bins - 1);
				histogram[bin]++;
			}

			double sum = 0.0;
			for (int i = 0; i < bins; i++)
			{
				sum += i * histogram[i];
			}

			double sumBackground = 0.0;
			int weightBackground = 0;
			int total = values.Length;
			double bestVariance = double.NegativeInfinity;
			int bestThreshold = bins / 2;
			for (int threshold = 0; threshold < bins; threshold++)
			{
				weightBackground += histogram[threshold];
				if (weightBackground == 0)
				{
					continue;
				}

				int weightForeground = total - weightBackground;
				if (weightForeground == 0)
				{
					break;
				}

				sumBackground += threshold * histogram[threshold];
				double meanBackground = sumBackground / weightBackground;
				double meanForeground = (sum - sumBackground) / weightForeground;
				double delta = meanBackground - meanForeground;
				double variance = weightBackground * weightForeground * delta * delta;
				if (variance > bestVariance)
				{
					bestVariance = variance;
					bestThreshold = threshold;
				}
			}

			return bestThreshold / (double)(bins - 1);
		}

		private static double GetGlyphPressurePenalty(
			int uniqueGlyphCount,
			Gif320RenderOptions options
		)
		{
			if (uniqueGlyphCount <= options.MaxGlyphs)
			{
				return 0.0;
			}

			double cells = Math.Max(1.0, options.CellsX * options.CellsY);
			return Math.Min(0.18, (uniqueGlyphCount - options.MaxGlyphs) / cells * 0.30);
		}

		private static void InsertFinalist(
			List<ScoredSettings> finalists,
			ScoredSettings candidate,
			int limit
		)
		{
			int index = finalists.Count;
			for (int i = 0; i < finalists.Count; i++)
			{
				if (candidate.Score > finalists[i].Score)
				{
					index = i;
					break;
				}
			}

			finalists.Insert(index, candidate);
			if (finalists.Count > limit)
			{
				finalists.RemoveAt(finalists.Count - 1);
			}
		}

		private static string GetSettingsKey(Gif320ToneSettings settings)
		{
			return string.Join(
				"|",
				settings.RedWeight.ToString("F4", CultureInfo.InvariantCulture),
				settings.GreenWeight.ToString("F4", CultureInfo.InvariantCulture),
				settings.BlueWeight.ToString("F4", CultureInfo.InvariantCulture),
				settings.Gamma.ToString("F3", CultureInfo.InvariantCulture),
				settings.Contrast.ToString("F3", CultureInfo.InvariantCulture),
				settings.Brightness.ToString("F3", CultureInfo.InvariantCulture),
				settings.Threshold.ToString("F3", CultureInfo.InvariantCulture),
				settings.HalfThreshold.ToString("F3", CultureInfo.InvariantCulture),
				settings.DitherMode.ToString(),
				settings.UseLocalContrast.ToString()
			);
		}

		private static double[] NormalizeBalance(
			double red,
			double green,
			double blue
		)
		{
			red = Math.Max(0.0, red);
			green = Math.Max(0.0, green);
			blue = Math.Max(0.0, blue);
			double sum = red + green + blue;
			if (sum <= 0.0)
			{
				return new[] { 0.2126, 0.7152, 0.0722 };
			}

			return new[] { red / sum, green / sum, blue / sum };
		}

		private static double Bilinear(
			double topLeft,
			double topRight,
			double bottomLeft,
			double bottomRight,
			double x,
			double y
		)
		{
			double top = topLeft + (topRight - topLeft) * x;
			double bottom = bottomLeft + (bottomRight - bottomLeft) * x;
			return top + (bottom - top) * y;
		}

		private static int HammingDistance(ulong[] left, ulong[] right)
		{
			int distance = 0;
			for (int i = 0; i < left.Length; i++)
			{
				distance += PopCount(left[i] ^ right[i]);
			}

			return distance;
		}

		private static int InvertedHammingDistance(ulong[] left, ulong[] right)
		{
			int distance = 0;
			for (int i = 0; i < left.Length; i++)
			{
				distance += PopCount((left[i] ^ ~right[i]) & GlyphBitMasks[i]);
			}

			return distance;
		}

		private static int PopCount(ulong value)
		{
			value -= (value >> 1) & 0x5555555555555555UL;
			value = (value & 0x3333333333333333UL)
				+ ((value >> 2) & 0x3333333333333333UL);
			return (int)((((value + (value >> 4)) & 0x0f0f0f0f0f0f0f0fUL)
				* 0x0101010101010101UL) >> 56);
		}

		private static ulong[] PackBits(bool[] pixels)
		{
			var packed = new ulong[(pixels.Length + 63) / 64];
			for (int i = 0; i < pixels.Length; i++)
			{
				if (pixels[i])
				{
					packed[i / 64] |= 1UL << (i & 63);
				}
			}

			return packed;
		}

		private static ulong[][] PackCenters(List<bool[]> centers)
		{
			var packed = new ulong[centers.Count][];
			for (int i = 0; i < centers.Count; i++)
			{
				packed[i] = PackBits(centers[i]);
			}

			return packed;
		}

		private static ulong[] CreateGlyphBitMasks()
		{
			var masks = new ulong[(BitsPerGlyph + 63) / 64];
			int remainingBits = BitsPerGlyph;
			for (int i = 0; i < masks.Length; i++)
			{
				int bits = Math.Min(64, remainingBits);
				masks[i] = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
				remainingBits -= bits;
			}

			return masks;
		}

		private static bool[] CopyPixels(bool[] pixels)
		{
			var copy = new bool[pixels.Length];
			Array.Copy(pixels, copy, pixels.Length);
			return copy;
		}

		private static int BytesPerPixel(Gif320PixelFormat format)
		{
			switch (format)
			{
				case Gif320PixelFormat.Rgb24:
					return 3;
				case Gif320PixelFormat.Rgba32:
				case Gif320PixelFormat.Bgra32:
					return 4;
				default:
					throw new ArgumentOutOfRangeException(nameof(format));
			}
		}

		private static int ClampInt(int value, int min, int max)
		{
			if (value < min)
			{
				return min;
			}

			if (value > max)
			{
				return max;
			}

			return value;
		}

		private static double Clamp01(double value)
		{
			if (value <= 0.0)
			{
				return 0.0;
			}

			if (value >= 1.0)
			{
				return 1.0;
			}

			return value;
		}

		private static double[] CreateSrgbToLinearTable()
		{
			var table = new double[256];
			for (int i = 0; i < table.Length; i++)
			{
				double value = i / 255.0;
				table[i] = value <= 0.04045
					? value / 12.92
					: Math.Pow((value + 0.055) / 1.055, 2.4);
			}

			return table;
		}

		private sealed class LinearImage
		{
			public LinearImage(
				int width,
				int height,
				double[] red,
				double[] green,
				double[] blue
			)
			{
				Width = width;
				Height = height;
				Red = red;
				Green = green;
				Blue = blue;
			}

			public int Width { get; }

			public int Height { get; }

			public double[] Red { get; }

			public double[] Green { get; }

			public double[] Blue { get; }
		}

		private sealed class RenderedBitmap
		{
			public RenderedBitmap(double[] reference, bool[] bitmap)
			{
				Reference = reference;
				Bitmap = bitmap;
			}

			public double[] Reference { get; }

			public bool[] Bitmap { get; }
		}

		private sealed class GlyphPattern
		{
			private string? _sixelPattern;

			public GlyphPattern(bool[] pixels, ulong[] packedBits)
			{
				Pixels = pixels;
				PackedBits = packedBits;
			}

			public bool[] Pixels { get; }

			public ulong[] PackedBits { get; }

			public string SixelPattern
			{
				get
				{
					return _sixelPattern ??= EncodeSixelPattern(Pixels);
				}
			}

			public int Weight { get; set; }
		}

		private readonly struct GlyphKey : IEquatable<GlyphKey>
		{
			private readonly ulong _a;
			private readonly ulong _b;
			private readonly ulong _c;

			public GlyphKey(ulong[] packedBits)
			{
				_a = packedBits.Length > 0 ? packedBits[0] : 0UL;
				_b = packedBits.Length > 1 ? packedBits[1] : 0UL;
				_c = packedBits.Length > 2 ? packedBits[2] : 0UL;
			}

			public bool Equals(GlyphKey other)
			{
				return _a == other._a && _b == other._b && _c == other._c;
			}

			public override bool Equals(object? obj)
			{
				return obj is GlyphKey other && Equals(other);
			}

			public override int GetHashCode()
			{
				unchecked
				{
					int hash = 17;
					hash = (hash * 31) + _a.GetHashCode();
					hash = (hash * 31) + _b.GetHashCode();
					hash = (hash * 31) + _c.GetHashCode();
					return hash;
				}
			}
		}

		private sealed class PackedScreen
		{
			public PackedScreen(
				List<GlyphPattern> glyphs,
				int[] cellGlyphIndexes,
				bool[] cellReverseVideo,
				int uniqueGlyphCount,
				double reductionErrorPerCellPixel,
				double highReductionErrorPerCellPixel,
				double worstReductionErrorPerCellPixel
			)
			{
				Glyphs = glyphs;
				CellGlyphIndexes = cellGlyphIndexes;
				CellReverseVideo = cellReverseVideo;
				UniqueGlyphCount = uniqueGlyphCount;
				ReductionErrorPerCellPixel = reductionErrorPerCellPixel;
				HighReductionErrorPerCellPixel = highReductionErrorPerCellPixel;
				WorstReductionErrorPerCellPixel = worstReductionErrorPerCellPixel;
			}

			public List<GlyphPattern> Glyphs { get; }

			public int[] CellGlyphIndexes { get; }

			public bool[] CellReverseVideo { get; }

			public int UniqueGlyphCount { get; }

			public double ReductionErrorPerCellPixel { get; }

			public double HighReductionErrorPerCellPixel { get; }

			public double WorstReductionErrorPerCellPixel { get; }
		}

		private sealed class VectorQuantizationResult
		{
			public VectorQuantizationResult(
				List<GlyphPattern> glyphs,
				int[] uniqueToGlyph,
				double errorPerCellPixel,
				double highErrorPerCellPixel,
				double worstErrorPerCellPixel
			)
			{
				Glyphs = glyphs;
				UniqueToGlyph = uniqueToGlyph;
				ErrorPerCellPixel = errorPerCellPixel;
				HighErrorPerCellPixel = highErrorPerCellPixel;
				WorstErrorPerCellPixel = worstErrorPerCellPixel;
			}

			public List<GlyphPattern> Glyphs { get; }

			public int[] UniqueToGlyph { get; }

			public double ErrorPerCellPixel { get; }

			public double HighErrorPerCellPixel { get; }

			public double WorstErrorPerCellPixel { get; }
		}

		private readonly struct WeightedCellError
		{
			public WeightedCellError(double error, int weight)
			{
				Error = error;
				Weight = weight;
			}

			public double Error { get; }

			public int Weight { get; }
		}

		private readonly struct ReductionErrorStats
		{
			public ReductionErrorStats(double average, double high, double worst)
			{
				Average = Clamp01(average);
				High = Clamp01(high);
				Worst = Clamp01(worst);
				FairnessCost = Average
					+ High * 0.55
					+ Worst * 1.15
					+ Worst * Worst * 0.70;
			}

			public double Average { get; }

			public double High { get; }

			public double Worst { get; }

			public double FairnessCost { get; }
		}

		private sealed class ScoredSettings
		{
			public ScoredSettings(Gif320ToneSettings settings, double score)
			{
				Settings = settings;
				Score = score;
			}

			public Gif320ToneSettings Settings { get; }

			public double Score { get; }
		}
	}
}
