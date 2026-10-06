using MudBlazor;

namespace SmtpMockup.Web.Theme;

/// <summary>
/// Tema Material You dark de la UI, con los tokens exactos del prototipo de diseño
/// (<c>docs/UI-Prototype/index.html</c>): paleta teal/azul sobre superficies gris azuladas, tipografía
/// Inter con la escala del export y radios de 12px. Es la fuente de verdad visual de la app: si un
/// color cambia, cambia aquí y en el registro de cambios de <c>DESIGN.md</c>, nunca en un componente.
/// </summary>
/// <remarks>
/// Sólo cubre lo que MudBlazor dibuja (botones, inputs, tablas, chips…). El resto del sistema visual
/// —tokens <c>--md-*</c>, nav rail, barra superior, tarjetas, motion— vive en <c>wwwroot/app.css</c>,
/// que copia las variables del export tal cual. Dos excepciones documentadas frente al prototipo:
/// el rol semántico <em>warning</em> (ámbar) no existe en el export y se añade para los estados de
/// aviso, y la tipografía se mapea a los quince huecos de MudBlazor (ver
/// <see cref="BuildTypography"/>).
/// </remarks>
public static class MockupTheme
{
    /// <summary>
    /// Familia tipográfica del export: Inter con respaldo del sistema. Sin la fuente de Google la
    /// app sigue siendo legible (un mockup local no puede depender de la red para verse bien).
    /// </summary>
    private static readonly string[] FontFamilyStack = ["Inter", "system-ui", "sans-serif"];

    /// <summary>Instancia única: el tema no tiene estado y crearlo en cada render sería trabajo muerto.</summary>
    public static MudTheme Instance { get; } = Build();

    private static MudTheme Build()
    {
        return new MudTheme
        {
            PaletteDark = BuildPalette(),
            Typography = BuildTypography(),
            LayoutProperties = new LayoutProperties
            {
                // --md-shape-md del export: el radio que usan paneles, tarjetas e inputs.
                DefaultBorderRadius = "12px",
                AppbarHeight = "64px",
                DrawerWidthLeft = "240px",
                DrawerWidthRight = "240px",
                DrawerMiniWidthLeft = "80px",
                DrawerMiniWidthRight = "80px",
            },
        };
    }

    /// <summary>Paleta dark del export, con los contrastes del mismo tonal palette.</summary>
    private static PaletteDark BuildPalette()
    {
        return new PaletteDark
        {
            // Roles primarios del tonal palette teal/azul del export.
            Primary = "#4DD0E1",
            PrimaryContrastText = "#00363A",
            PrimaryLighten = "#7FE7F2",
            PrimaryDarken = "#00A6BE",
            Secondary = "#80CBC4",
            SecondaryContrastText = "#003731",
            SecondaryLighten = "#A0F0E7",
            SecondaryDarken = "#4A9D96",
            Tertiary = "#80CBF4",
            TertiaryContrastText = "#00344F",
            TertiaryLighten = "#BEE6FF",
            TertiaryDarken = "#0094D0",

            // Estados semánticos. Success y Error salen del export; Warning no existe en él y se
            // añade (ámbar) para que los avisos no se confundan con errores.
            Success = "#81C784",
            SuccessContrastText = "#1B3A2A",
            SuccessLighten = "#A5D6A7",
            SuccessDarken = "#4CAF50",
            Warning = "#FFCF66",
            WarningContrastText = "#452C00",
            WarningLighten = "#FFE09A",
            WarningDarken = "#C79B2E",
            Error = "#FFB4AB",
            ErrorContrastText = "#690005",
            ErrorLighten = "#EF9A9A",
            ErrorDarken = "#C57873",
            Info = "#80CBF4",
            InfoContrastText = "#00344F",
            InfoLighten = "#BEE6FF",
            InfoDarken = "#0094D0",

            // Superficies: --md-surface y sus contenedores, de menor a mayor elevación tonal.
            Background = "#111318",
            BackgroundGray = "#1D2024",
            Surface = "#111318",
            Dark = "#333539",
            DarkContrastText = "#E2E2E9",
            DarkLighten = "#43474E",
            DarkDarken = "#282A2E",
            AppbarBackground = "#1D2024",
            AppbarText = "#E2E2E9",
            DrawerBackground = "#111318",
            DrawerText = "#C3C6CF",
            DrawerIcon = "#8D9199",

            // Texto y acciones: --md-on-surface, --md-on-surface-variant y --md-outline.
            TextPrimary = "#E2E2E9",
            TextSecondary = "#C3C6CF",
            TextDisabled = "#8D9199",
            ActionDefault = "#C3C6CF",
            ActionDisabled = "#8D9199",
            ActionDisabledBackground = "#282A2E",

            // Líneas y tablas: --md-outline-variant / --md-outline.
            LinesDefault = "#43474E",
            LinesInputs = "#8D9199",
            Divider = "#43474E",
            DividerLight = "#282A2E",
            TableLines = "#43474E",
            TableStriped = "#191C20",
            TableHover = "rgba(227,226,233,0.08)",

            OverlayDark = "rgba(0,0,0,0.6)",
            OverlayLight = "rgba(0,0,0,0.3)",
            Skeleton = "#282A2E",

            // Grises de soporte (tooltips, chips neutros, focus rings).
            GrayDefault = "#8D9199",
            GrayLight = "#C3C6CF",
            GrayLighter = "#E2E2E9",
            GrayDark = "#43474E",
            GrayDarker = "#1D2024",
            Black = "#000000",
            White = "#FFFFFF",
        };
    }

    /// <summary>
    /// Escala tipográfica del export sobre los quince huecos de MudBlazor. El prototipo define seis
    /// roles (display, headline, title, body, label, label-sm) que aquí se reparten así:
    /// H1←headline, H2–H3 escalones intermedios, H4←title, Body1/Default←body, Subtitle2/Button←label,
    /// Caption/Overline←label-sm. El «text-transform» de los botones baja a «none» a propósito: el
    /// uppercase por defecto de MudBlazor rompe el copy del export («Get Started», no «GET STARTED»).
    /// </summary>
    private static Typography BuildTypography()
    {
        var typography = new Typography();

        Apply(typography.Default, fontSize: "0.9375rem", fontWeight: "400", lineHeight: "1.6", letterSpacing: ".01em");
        Apply(typography.H1, fontSize: "2rem", fontWeight: "600", lineHeight: "1.25", letterSpacing: "-.01em");
        Apply(typography.H2, fontSize: "1.5rem", fontWeight: "600", lineHeight: "1.3", letterSpacing: "-.01em");
        Apply(typography.H3, fontSize: "1.25rem", fontWeight: "600", lineHeight: "1.35", letterSpacing: "-.005em");
        Apply(typography.H4, fontSize: "1.125rem", fontWeight: "600", lineHeight: "1.4", letterSpacing: "0");
        Apply(typography.H5, fontSize: "1rem", fontWeight: "600", lineHeight: "1.5", letterSpacing: "0");
        Apply(typography.H6, fontSize: "0.9375rem", fontWeight: "600", lineHeight: "1.5", letterSpacing: "0");
        Apply(typography.Subtitle1, fontSize: "0.9375rem", fontWeight: "500", lineHeight: "1.5", letterSpacing: "0");
        Apply(typography.Subtitle2, fontSize: "0.8125rem", fontWeight: "500", lineHeight: "1.4", letterSpacing: ".02em");
        Apply(typography.Body1, fontSize: "0.9375rem", fontWeight: "400", lineHeight: "1.6", letterSpacing: ".01em");
        Apply(typography.Body2, fontSize: "0.8125rem", fontWeight: "400", lineHeight: "1.5", letterSpacing: ".02em");
        Apply(typography.Button, fontSize: "0.8125rem", fontWeight: "500", lineHeight: "1.4", letterSpacing: ".02em", textTransform: "none");
        Apply(typography.Caption, fontSize: "0.6875rem", fontWeight: "500", lineHeight: "1.4", letterSpacing: ".02em");
        Apply(typography.Overline, fontSize: "0.6875rem", fontWeight: "500", lineHeight: "1.4", letterSpacing: ".08em", textTransform: "uppercase");

        return typography;
    }

    /// <summary>
    /// Aplica la familia Inter y la escala a un hueco tipográfico. Cada hueco trae su propia familia
    /// por defecto (Roboto), así que el <c>FontFamily</c> hay que setearlo en todos y no sólo en
    /// <c>Default</c>: setearlo únicamente ahí deja los títulos en la fuente equivocada.
    /// </summary>
    private static void Apply(
        BaseTypography slot,
        string fontSize,
        string fontWeight,
        string lineHeight,
        string letterSpacing,
        string? textTransform = null)
    {
        slot.FontFamily = FontFamilyStack;
        slot.FontSize = fontSize;
        slot.FontWeight = fontWeight;
        slot.LineHeight = lineHeight;
        slot.LetterSpacing = letterSpacing;

        if (textTransform is not null)
        {
            slot.TextTransform = textTransform;
        }
    }
}
