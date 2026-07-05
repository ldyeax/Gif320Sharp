using System.Collections.Generic;

namespace Gif320Sharp_Core
{
	public sealed class Gif320RenderResult
	{
		internal Gif320RenderResult(
			string vtSequence,
			string[] screenRows,
			IReadOnlyList<string> glyphSixelPatterns,
			bool[]? reverseVideoCells,
			Gif320ToneSettings toneSettings,
			int cellsX,
			int cellsY,
			int uniqueGlyphCountBeforeReduction,
			string glyphAtlas,
			string cellMap,
			double score,
			double reductionErrorPerCellPixel,
			double highReductionErrorPerCellPixel,
			double worstReductionErrorPerCellPixel
		)
		{
			VtSequence = vtSequence;
			ScreenRows = screenRows;
			GlyphSixelPatterns = glyphSixelPatterns;
			ReverseVideoCells = reverseVideoCells ?? new bool[cellsX * cellsY];
			ToneSettings = toneSettings;
			CellsX = cellsX;
			CellsY = cellsY;
			UniqueGlyphCountBeforeReduction = uniqueGlyphCountBeforeReduction;
			GlyphAtlas = glyphAtlas;
			CellMap = cellMap;
			Score = score;
			ReductionErrorPerCellPixel = reductionErrorPerCellPixel;
			HighReductionErrorPerCellPixel = highReductionErrorPerCellPixel;
			WorstReductionErrorPerCellPixel = worstReductionErrorPerCellPixel;
		}

		public string VtSequence { get; }

		public string[] ScreenRows { get; }

		public IReadOnlyList<string> GlyphSixelPatterns { get; }

		public bool[] ReverseVideoCells { get; }

		public Gif320ToneSettings ToneSettings { get; }

		public int CellsX { get; }

		public int CellsY { get; }

		public int GlyphCount => GlyphSixelPatterns.Count;

		public int UniqueGlyphCountBeforeReduction { get; }

		public bool WasGlyphReduced => UniqueGlyphCountBeforeReduction > GlyphCount;

		public string GlyphAtlas { get; }

		public string CellMap { get; }

		public double Score { get; }

		public double ReductionErrorPerCellPixel { get; }

		public double HighReductionErrorPerCellPixel { get; }

		public double WorstReductionErrorPerCellPixel { get; }
	}
}
