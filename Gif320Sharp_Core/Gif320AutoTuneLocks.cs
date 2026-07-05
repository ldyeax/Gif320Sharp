using System;

namespace Gif320Sharp_Core
{
	[Flags]
	public enum Gif320AutoTuneLocks
	{
		None = 0,
		RedBalance = 1 << 0,
		GreenBalance = 1 << 1,
		BlueBalance = 1 << 2,
		FullThreshold = 1 << 3,
		HalfThreshold = 1 << 4,
		Balance = RedBalance | GreenBalance | BlueBalance,
		Thresholds = FullThreshold | HalfThreshold,
		Tone = Balance | Thresholds,
	}
}
