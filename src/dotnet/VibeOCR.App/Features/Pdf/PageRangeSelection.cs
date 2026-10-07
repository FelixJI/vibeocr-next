using System.Globalization;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Pdf;

internal static class PageRangeSelection
{
    public static string Validate(string value)
    {
        string range = value.Trim().ToLowerInvariant();
        if (!MineruRecognitionSettings.IsValidPageRange(range))
            throw new ArgumentException("页码范围请填写 all 或 1,3-5；r1 表示最后一页。");
        return range;
    }

    public static int[] Resolve(string value, int pageCount)
    {
        string range = Validate(value);
        if (pageCount <= 0) throw new ArgumentException("文件没有可识别的页面。");
        if (range == "all") return Enumerable.Range(0, pageCount).ToArray();
        var pages = new SortedSet<int>();
        foreach (string part in range.Split(','))
        {
            string[] bounds = part.Split('-');
            int start = Page(bounds[0], pageCount);
            int end = bounds.Length == 1 ? start : Page(bounds[1], pageCount);
            if (start > end) throw new ArgumentException("页码范围起始页不能大于结束页。");
            for (int index = start; index <= end; index++) pages.Add(index);
        }
        return [.. pages];
    }

    private static int Page(string text, int count)
    {
        bool reverse = text.StartsWith('r');
        if (!int.TryParse(reverse ? text[1..] : text, NumberStyles.None,
            CultureInfo.InvariantCulture, out int number) || number < 1 || number > count)
            throw new ArgumentException($"页码超出文件范围（共 {count} 页）。");
        return reverse ? count - number : number - 1;
    }
}
