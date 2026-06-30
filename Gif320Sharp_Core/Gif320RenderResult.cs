using System.Collections.Generic;

namespace Gif320Sharp_Core
{
	public sealed class Gif320RenderResult
	{
		internal Gif320RenderResult(
			string vtSequence,
			string[] screenRows,
			IReadOnlyList<string> glyphSixelPatterns,
			Gif320ToneSettings toneSettings,
			int cellsX,
			int cellsY,
			int uniqueGlyphCountBeforeReduction,
			double score,
			double reductionErrorPerCellPixel
		)
		{
			VtSequence = vtSequence;
			ScreenRows = screenRows;
			GlyphSixelPatterns = glyphSixelPatterns;
			ToneSettings = toneSettings;
			CellsX = cellsX;
			CellsY = cellsY;
			UniqueGlyphCountBeforeReduction = uniqueGlyphCountBeforeReduction;
			Score = score;
			ReductionErrorPerCellPixel = reductionErrorPerCellPixel;
		}

		public string VtSequence { get; }

		public string[] ScreenRows { get; }

		public IReadOnlyList<string> GlyphSixelPatterns { get; }

		public Gif320ToneSettings ToneSettings { get; }

		public int CellsX { get; }

		public int CellsY { get; }

		public int GlyphCount => GlyphSixelPatterns.Count;

		public int UniqueGlyphCountBeforeReduction { get; }

		public bool WasGlyphReduced => UniqueGlyphCountBeforeReduction > GlyphCount;

		public double Score { get; }

		public double ReductionErrorPerCellPixel { get; }
	}
}
