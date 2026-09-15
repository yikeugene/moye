using Moye.Models;

namespace Moye.ViewModels;

public sealed class SectionViewModel(NoteSection section, int number, int pageCount)
{
    public NoteSection Section { get; } = section;
    public string Id => Section.Id;
    public string Title => Section.Title;
    public int Number { get; } = number;
    public int PageCount { get; } = pageCount;
    public string PageCountText => $"{PageCount} {(PageCount == 1 ? "page" : "pages")}";
}
