using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CxSshClient.Models;

/// <summary>传输队列条目</summary>
public class TransferItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool IsUpload { get; set; }
    public string Glyph => IsUpload ? "\uE898" : "\uE896";

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; Notify(nameof(Progress)); }
    }

    private string _status = "等待";
    public string StatusText
    {
        get => _status;
        set { _status = value; Notify(nameof(StatusText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
