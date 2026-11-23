using System.IO;
using FanMaxxxing.Core;
using WinForms = System.Windows.Forms;

namespace FanMaxxxing.UI;

/// <summary>Icono en la bandeja del sistema con menú rápido. Se libera siempre al salir (si no, queda un icono fantasma).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly System.Drawing.Icon? _iconImage;
    private readonly WinForms.ToolStripMenuItem _coolItem, _coolOffItem, _cancelItem;
    private bool _disposed;

    public TrayIcon(App app)
    {
        _app = app;
        _iconImage = LoadIcon();

        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        var open = new WinForms.ToolStripMenuItem("Abrir FanMaxxxing", null, (_, _) => _app.ShowMain());
        _coolItem = new WinForms.ToolStripMenuItem("❄  Enfriar ahora", null, (_, _) => _app.StartFromTray(AfterAction.Nothing));
        _coolOffItem = new WinForms.ToolStripMenuItem("🌙  Enfriar y apagar", null, (_, _) => _app.StartFromTray(AfterAction.Shutdown));
        _cancelItem = new WinForms.ToolStripMenuItem("✋  Cancelar enfriamiento", null, (_, _) => _app.CancelFromTray());
        var exit = new WinForms.ToolStripMenuItem("Salir", null, (_, _) => _app.RequestExit());

        _menu.Items.Add(open);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_coolItem);
        _menu.Items.Add(_coolOffItem);
        _menu.Items.Add(_cancelItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exit);
        _menu.Opening += OnMenuOpening;

        _icon = new WinForms.NotifyIcon
        {
            Icon = _iconImage ?? System.Drawing.SystemIcons.Application,
            Text = "FanMaxxxing",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += OnMouseClick;
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left) _app.ShowMain();
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool cooling = _app.IsCooling;
        int min = _app.Settings.DurationMinutes;
        _coolItem.Text = $"❄  Enfriar ahora ({min} min)";
        _coolOffItem.Text = $"🌙  Enfriar {min} min y apagar";
        _coolItem.Enabled = !cooling;
        _coolOffItem.Enabled = !cooling;
        _cancelItem.Visible = cooling;
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/fan.ico"));
            if (info == null) return null;
            using var s = info.Stream;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return new System.Drawing.Icon(ms, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo cargar el icono de la bandeja: " + ex.Message);
            return null;
        }
    }

    public void Balloon(string title, string text)
    {
        if (_disposed) return;
        try { _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.None); }
        catch { /* ignorar */ }
    }

    public void SetTooltip(string text)
    {
        if (_disposed) return;
        // NotifyIcon.Text admite como mucho 127 caracteres (63 en versiones viejas)
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu.Opening -= OnMenuOpening;
        _icon.MouseClick -= OnMouseClick;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage?.Dispose();
    }
}
