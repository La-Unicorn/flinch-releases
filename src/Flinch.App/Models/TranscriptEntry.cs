using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Flinch_App;

public sealed class TranscriptEntry
{
    public required string Sender { get; set; }
    public required string Message { get; set; }
    public required string Timestamp { get; set; }
    public HorizontalAlignment Alignment { get; set; }
    public required SolidColorBrush BackgroundBrush { get; set; }
    public required SolidColorBrush BorderBrush { get; set; }
    public required SolidColorBrush SenderForegroundBrush { get; set; }
}
