using Neo.IPC;

namespace Neo.App
{
    public sealed class DesignerApplyRequestedEventArgs : EventArgs
    {
        public DesignerSelectionMessage Selection { get; }
        public IReadOnlyDictionary<string, string> Updates { get; }

        public DesignerApplyRequestedEventArgs(DesignerSelectionMessage selection, IReadOnlyDictionary<string, string> updates)
        {
            Selection = selection ?? throw new ArgumentNullException(nameof(selection));
            Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        }
    }
}
