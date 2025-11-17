// CartridgeModels.cs
using System.Collections.Generic;

namespace Sorter
{
    public class CartridgeConfig
    {
        public string Name { get; set; } = "";
        public List<HeadstampConfig> Headstamps { get; set; } = new List<HeadstampConfig>();
    }

    public class HeadstampConfig
    {
        public int Bin { get; set; }
        public string Label { get; set; } = "";
    }
}
