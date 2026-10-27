using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace GitLabTodo.Ui;

public sealed partial class Look : Styles
{
    public Look() => AvaloniaXamlLoader.Load(this);
}
