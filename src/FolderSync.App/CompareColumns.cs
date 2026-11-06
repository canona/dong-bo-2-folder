using System.ComponentModel;
using System.Windows;

namespace FolderSync.App;

/// <summary>
/// Do rong cot cua cay so sanh - MOT nguon cho ca dong tieu de (TreeHeaderGrid) va DataTemplate cua moi dong.
/// Truoc day 2 noi ghi cung 8 do rong va phai sua khop bang tay; nay ca 2 bind vao day nen keo cot o tieu de
/// thi moi dong (ke ca dong duoc tai su dung khi cuon - ListBox ao hoa Recycling) doi theo ngay.
/// Kieu Explorer: keo mep phai cot nao thi chi cot do doi, cac cot sau dich theo; cot cuoi (Ly do) = phan con lai.
/// </summary>
public sealed class CompareColumns : INotifyPropertyChanged
{
    // Cot 0..6: Muc, L Size, L Date, Chieu, R Size, R Date, Trang thai. Cot 7 (Ly do) la '*'.
    public const int Count = 7;
    public static readonly double[] Defaults = { 340, 96, 128, 44, 96, 128, 90 };
    // Toi thieu de khong keo mat han 1 cot (khong con cho de bam lai mep cot)
    private static readonly double[] Minimums = { 120, 40, 60, 28, 40, 60, 40 };
    private const double Maximum = 1600;

    private readonly double[] _widths = (double[])Defaults.Clone();

    public GridLength C0 => new(_widths[0]);
    public GridLength C1 => new(_widths[1]);
    public GridLength C2 => new(_widths[2]);
    public GridLength C3 => new(_widths[3]);
    public GridLength C4 => new(_widths[4]);
    public GridLength C5 => new(_widths[5]);
    public GridLength C6 => new(_widths[6]);

    public double Get(int index) => _widths[index];

    public void Set(int index, double width)
    {
        if (index is < 0 or >= Count || double.IsNaN(width) || double.IsInfinity(width)) return;
        width = Math.Clamp(Math.Round(width), Minimums[index], Maximum);
        if (_widths[index] == width) return;
        _widths[index] = width;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("C" + index));
    }

    public void Reset()
    {
        for (int i = 0; i < Count; i++) Set(i, Defaults[i]);
    }

    public bool IsDefault => _widths.SequenceEqual(Defaults);

    /// <summary>Nap tu app-settings.json - sai do dai / gia tri hong thi bo qua (giu mac dinh), khong lam sap app.</summary>
    public void Load(double[]? saved)
    {
        if (saved is null || saved.Length != Count) return;
        for (int i = 0; i < Count; i++) Set(i, saved[i]);
    }

    public double[] Snapshot() => (double[])_widths.Clone();

    public event PropertyChangedEventHandler? PropertyChanged;
}
