// PatchOperation.cs
using Newtonsoft.Json;
using System;

namespace Neo.App
{
    /// <summary>
    /// Repräsentiert eine einzelne Patch-Operation, die von der KI generiert wird.
    /// </summary>
    public class PatchOperation
    {
        /// <summary>
        /// Die Art der Operation: "REPLACE", "ADD", oder "DELETE".
        /// </summary>
        [JsonProperty("operation")]
        public string Operation { get; set; } = string.Empty;

        /// <summary>
        /// Die eindeutige Signatur des Code-Elements, das modifiziert werden soll.
        /// z.B. "public void MyMethod(int value)" oder "public class MyClass".
        /// Wird für REPLACE und DELETE verwendet.
        /// </summary>
        [JsonProperty("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// Für 'ADD'-Operationen: Die Signatur des Elternelements, dem der neue Code
        /// hinzugefügt werden soll (z.B. die Signatur der Klasse).
        /// </summary>
        [JsonProperty("parent_signature")]
        public string ParentSignature { get; set; } = string.Empty;

        /// <summary>
        /// Der vollständige neue Quelltext für das Code-Element.
        /// Wird für REPLACE und ADD verwendet.
        /// </summary>
        [JsonProperty("new_content")]
        public string NewContent { get; set; } = string.Empty;
    }
}