namespace ShipEditor.Model
{
	public static class CellIndex
	{
		public static ulong FromXY(int x, int y) => ((ulong)(uint)y << 32) + (uint)x;
		
		public static void GetXY(ulong index, out int x, out int y)
		{
			y = (int)(index >> 32);
			x = (int)(index & 0xFFFFFFFF);
		}
	}
}
