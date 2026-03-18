using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public interface IStatefulControl
    {
        /// <summary>
        /// Gibt den aktuellen Zustand des Controls in einem serialisierbaren Format (z. B. JSON) zurück.
        /// </summary>
        public string GetState();
    }
}
