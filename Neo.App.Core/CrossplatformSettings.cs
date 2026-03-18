using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public sealed record CrossplatformSettings
    {
        public bool UseAvalonia { get; set; } = false;
        public bool UsePython { get; set; } = false;
    }
}
