namespace PinBoard.Models;

public enum ClipItemKind
{
    Text,
    Image,
    Files,
    Rich   // has HTML / RTF alongside text (e.g. Office, browser)
}
