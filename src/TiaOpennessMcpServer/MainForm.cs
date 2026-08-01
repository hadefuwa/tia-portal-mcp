using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace TiaOpennessMcpServer;

public class MainForm : Form
{
    private readonly WebView2    _webView = new();
    private readonly NotifyIcon  _tray;
    private bool _closeForReal;

    public MainForm()
    {
        Text          = "TIA Portal Dashboard";
        Width         = 1440;
        Height        = 900;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize   = new Size(900, 600);

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open",  null, (_, _) => Restore());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit",  null, (_, _) => { _closeForReal = true; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = "TIA Portal Dashboard",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseDoubleClick += (_, _) => Restore();

        Load        += OnLoad;
        Resize      += OnResize;
        FormClosing += OnClosing;
    }

    private async void OnLoad(object sender, EventArgs e)
    {
        await _webView.EnsureCoreWebView2Async(null);
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.Source = new Uri("http://localhost:5000");
    }

    private void OnResize(object sender, EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) return;
        Hide();
        _tray.ShowBalloonTip(1500, "TIA Portal Dashboard",
            "Still running in the background. Double-click to reopen.", ToolTipIcon.Info);
    }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (_closeForReal || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        Hide();
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
