using System;
using System.Collections.Generic;
using System.Linq;
using Constructor;
using GameDatabase.Model;

namespace Gui.ComponentList
{
	public class SatellitesNode : IComponentTreeNode
	{
		public SatellitesNode(IComponentTreeNode parent, int locationIndex)
		{
			Parent = parent;
            Name = $"$GroupSatelliteN|{locationIndex+1}";
		}

		public IComponentTreeNode Parent { get; }

		public IComponentQuantityProvider QuantityProvider => null;

		public bool IsVisible { get; set; } = true;

		public string Name { get; }
        public SpriteId Icon => new SpriteId("icons/icon_scanner", SpriteId.Type.Default);
        public UnityEngine.Color Color => CommonNode.DefaultColor;
        public void Add(ComponentInfo componentInfo) { throw new InvalidOperationException(); }
        public int ItemCount => 10; // TODO;
        public IEnumerable<IComponentTreeNode> Nodes => Enumerable.Empty<IComponentTreeNode>();
        public IEnumerable<ComponentInfo> Components => Enumerable.Empty<ComponentInfo>();

        public void Clear() { }
	}
}
