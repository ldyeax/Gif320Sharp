using System;

namespace Gif320Sharp_Core
{
	public sealed class Gif320Exception : Exception
	{
		public Gif320Exception(string message)
			: base(message)
		{
		}

		public Gif320Exception(string message, Exception innerException)
			: base(message, innerException)
		{
		}
	}
}
