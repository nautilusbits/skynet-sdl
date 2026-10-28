using Android.App;
using Android.Views;
using Android.Widget;

namespace SkyNet.Android;

/// <summary>An answer of <see cref="Dialogs.Ask"/>.</summary>
internal enum Answer { Yes, No }

/// <summary>A dialog's title and message.</summary>
internal readonly record struct Prompt(string Title, string Text);

/// <summary>Android's dialogs, waited for from the setup's thread (they show on the UI thread):
/// platform Material in SkyNET's colours (Resources/values/styles.xml), beside the
/// <see cref="Backdrop"/>.</summary>
internal static class Dialogs
{
    /// <summary>A message with two buttons; returns the one tapped.</summary>
    public static Answer Ask(Activity activity, Prompt prompt, string yes, string no)
    {
        var answer = new TaskCompletionSource<Answer>();
        activity.RunOnUiThread(() => Present(activity, Build(activity, prompt.Title)
            .SetMessage(prompt.Text)!
            .SetPositiveButton(yes, (_, _) => answer.TrySetResult(Answer.Yes))!
            .SetNegativeButton(no, (_, _) => answer.TrySetResult(Answer.No))!));
        return answer.Task.Result;
    }

    /// <summary>A message with an OK button; returns once it is tapped.</summary>
    public static void Tell(Activity activity, Prompt prompt)
    {
        var closed = new TaskCompletionSource();
        activity.RunOnUiThread(() => Present(activity, Build(activity, prompt.Title)
            .SetMessage(prompt.Text)!
            .SetPositiveButton("OK", (_, _) => closed.TrySetResult())!));
        closed.Task.Wait();
    }

    /// <summary>A list of choices and a button to leave; returns the index tapped, or -1 for the button.</summary>
    public static int Choose(Activity activity, string title, string[] items, string leave)
    {
        var answer = new TaskCompletionSource<int>();
        activity.RunOnUiThread(() => Present(activity, Build(activity, title)
            .SetItems(items, (_, e) => answer.TrySetResult(e.Which))!
            .SetNegativeButton(leave, (_, _) => answer.TrySetResult(-1))!));
        return answer.Task.Result;
    }

    /// <summary>A progress without buttons; the caller updates and closes it.</summary>
    public static ProgressBox Show(Activity activity, string title, string first)
    {
        var shown = new TaskCompletionSource<ProgressBox>();
        activity.RunOnUiThread(() =>
        {
            var view = activity.LayoutInflater.Inflate(Resource.Layout.progress, null)!;
            view.FindViewById<TextView>(Resource.Id.progress_label)!.Text = first;
            var dialog = Present(activity, Build(activity, title).SetView(view)!);
            shown.SetResult(new ProgressBox(activity, dialog, view));
        });
        return shown.Task.Result;
    }

    private static AlertDialog.Builder Build(Activity activity, string title) =>
        new AlertDialog.Builder(activity, Resource.Style.SkyNetDialog)
            .SetTitle(title)!
            .SetCancelable(false)!;

    private static AlertDialog Present(Activity activity, AlertDialog.Builder builder)
    {
        var dialog = builder.Show()!;
        Backdrop.Place(activity, dialog);
        return dialog;
    }
}

/// <summary>A shown progress (<see cref="Dialogs.Show"/>): what is copied, a bar, done of all.
/// Updated from any thread.</summary>
internal sealed class ProgressBox(Activity activity, AlertDialog dialog, View view)
{
    private readonly TextView _label = view.FindViewById<TextView>(Resource.Id.progress_label)!;
    private readonly ProgressBar _bar = view.FindViewById<ProgressBar>(Resource.Id.progress_bar)!;
    private readonly TextView _count = view.FindViewById<TextView>(Resource.Id.progress_count)!;

    /// <summary>E.g. "MAP.210", 120 of 401; a total of 0 is unknown (the bar keeps moving).</summary>
    public void Update(string what, int done, int total) => activity.RunOnUiThread(() =>
    {
        _label.Text = what;
        _count.Text = total > 0 ? $"{done} of {total}" : $"{done} files";
        if (total <= 0)
        {
            return;
        }

        _bar.Indeterminate = false;
        _bar.Max = total;
        _bar.Progress = done;
    });

    public void Close() => activity.RunOnUiThread(dialog.Dismiss);
}
