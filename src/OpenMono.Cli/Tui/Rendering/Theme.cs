using Terminal.Gui.Drawing;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace OpenMono.Tui.Rendering;

public sealed class Theme
{

    public Color Background { get; init; } = Color.Black;
    public Color Foreground { get; init; } = Color.White;
    public Color Accent { get; init; } = Color.BrightBlue;
    public Color Muted { get; init; } = Color.Gray;
    public Color Error { get; init; } = Color.BrightRed;
    public Color Warning { get; init; } = Color.Yellow;
    public Color Success { get; init; } = Color.Green;

    public Color HeaderBg { get; init; } = Color.Black;
    public Color HeaderFg { get; init; } = Color.White;
    public Color StatusBarBg { get; init; } = Color.Black;
    public Color StatusBarFg { get; init; } = Color.White;
    public Color SidebarBg { get; init; } = Color.Black;
    public Color SidebarFg { get; init; } = Color.White;

    public Color UserBorder { get; init; } = Color.Green;
    public Color AssistantBorder { get; init; } = Color.Blue;
    public Color ToolBorder { get; init; } = Color.Gray;
    public Color SystemBorder { get; init; } = Color.Yellow;

    public Color SyntaxKeyword { get; init; } = Color.BrightBlue;
    public Color SyntaxString { get; init; } = Color.Green;
    public Color SyntaxNumber { get; init; } = new(209, 154, 102, 255);
    public Color SyntaxComment { get; init; } = Color.Gray;
    public Color SyntaxType { get; init; } = Color.BrightCyan;
    public Color SyntaxFunction { get; init; } = Color.BrightYellow;
    public Color SyntaxOperator { get; init; } = Color.White;

    public Color MdHeading { get; init; } = Color.BrightCyan;
    public Color MdLink { get; init; } = Color.BrightBlue;
    public Color MdInlineCodeFg { get; init; } = Color.BrightYellow;
    public Color MdInlineCodeBg { get; init; } = Color.DarkGray;
    public Color MdQuote { get; init; } = Color.Gray;
    public Color MdBullet { get; init; } = Color.BrightCyan;
    public Color CodeBlockBg { get; init; } = Color.Black;

    public TgAttribute Normal => new(Foreground, Background);
    public TgAttribute Bold => new(Foreground, Background, TextStyle.Bold);
    public TgAttribute Dim => new(Muted, Background);
    public TgAttribute Heading => new(MdHeading, Background, TextStyle.Bold);
    public TgAttribute InlineCode => new(MdInlineCodeFg, MdInlineCodeBg);
    public TgAttribute Link => new(MdLink, Background, TextStyle.Underline);
    public TgAttribute Quote => new(MdQuote, Background, TextStyle.Italic);
    public TgAttribute Bullet => new(MdBullet, Background);

    public TgAttribute GetSyntaxAttribute(TokenType token) => token switch
    {
        TokenType.Keyword  => new TgAttribute(SyntaxKeyword, CodeBlockBg),
        TokenType.String   => new TgAttribute(SyntaxString, CodeBlockBg),
        TokenType.Number   => new TgAttribute(SyntaxNumber, CodeBlockBg),
        TokenType.Comment  => new TgAttribute(SyntaxComment, CodeBlockBg),
        TokenType.Type     => new TgAttribute(SyntaxType, CodeBlockBg),
        TokenType.Function => new TgAttribute(SyntaxFunction, CodeBlockBg),
        TokenType.Operator => new TgAttribute(SyntaxOperator, CodeBlockBg),
        _                  => new TgAttribute(Foreground, CodeBlockBg),
    };

    public Scheme MakeRoleScheme(Color border) => new(new TgAttribute(border, Background))
    {
        Normal = new TgAttribute(border, Background),
        Focus = new TgAttribute(border, Background),
    };
}
