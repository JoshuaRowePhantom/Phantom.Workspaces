using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public sealed partial class AgentSessionModalStackControl : UserControl
{
    private AgentViewModel? owner;
    private CancellationTokenSource lifetimeCancellation = new();
    private readonly Dictionary<string, (AgentSessionModalViewModel Modal, Control Card)> cards =
        new(StringComparer.Ordinal);

    public AgentSessionModalStackControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += this.OnDataContextChanged;
        this.AttachedToVisualTree += this.OnAttachedToVisualTree;
        this.DetachedFromVisualTree += this.OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => this.BindOwner(this.DataContext as AgentViewModel);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => this.BindOwner(this.DataContext as AgentViewModel);

    private void BindOwner(AgentViewModel? value)
    {
        if (this.owner is not null)
        {
            ((INotifyCollectionChanged)this.owner.Modals).CollectionChanged -= this.OnModalsChanged;
        }

        this.owner = value;
        this.cards.Clear();
        if (this.owner is not null)
        {
            ((INotifyCollectionChanged)this.owner.Modals).CollectionChanged += this.OnModalsChanged;
        }
        this.RenderModals();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        this.BindOwner(null);
        this.lifetimeCancellation.Cancel();
        this.lifetimeCancellation.Dispose();
        this.lifetimeCancellation = new CancellationTokenSource();
    }

    private void OnModalsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        this.RenderModals();

    private void RenderModals()
    {
        if (this.owner is null)
        {
            this.ModalStack.Children.Clear();
            return;
        }

        var activeIds = this.owner.Modals.Select(modal => modal.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in this.cards.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            this.cards.Remove(staleId);
        }

        var orderedCards = new List<Control>(this.owner.Modals.Count);
        foreach (var modal in this.owner.Modals)
        {
            if (!this.cards.TryGetValue(modal.Id, out var entry)
                || !ReferenceEquals(entry.Modal, modal))
            {
                entry = (modal, this.CreateModalCard(modal));
                this.cards[modal.Id] = entry;
            }
            orderedCards.Add(entry.Card);
        }

        this.ModalStack.Children.Clear();
        foreach (var card in orderedCards)
        {
            this.ModalStack.Children.Add(card);
        }
    }

    private Control CreateModalCard(AgentSessionModalViewModel modal)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = modal.Title,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = modal.Body,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var error = new TextBlock
        {
            IsVisible = false,
            Foreground = Avalonia.Media.Brushes.OrangeRed,
        };
        var interactiveControls = new List<Control>();
        this.AddResponseControls(panel, modal, error, interactiveControls);
        panel.Children.Add(error);

        var card = new Border
        {
            Margin = new Thickness(0, 4),
            Padding = new Thickness(12),
            Child = panel,
        };
        card.Classes.Add("agent-surface");
        AutomationProperties.SetName(card, modal.Title);
        return card;
    }

    private void AddResponseControls(
        StackPanel panel,
        AgentSessionModalViewModel modal,
        TextBlock error,
        List<Control> interactiveControls)
    {
        switch (modal.Content)
        {
            case FreeformModalContent freeform:
            {
                var input = new TextBox { PlaceholderText = freeform.Placeholder };
                AutomationProperties.SetName(input, $"{modal.Title} response");
                var submit = this.CreateButton("Submit response", "Submit");
                submit.IsEnabled = !freeform.IsRequired;
                input.TextChanged += (_, _) =>
                    submit.IsEnabled = !freeform.IsRequired || !string.IsNullOrWhiteSpace(input.Text);
                interactiveControls.Add(input);
                interactiveControls.Add(submit);
                submit.Click += async (_, _) => await this.RespondAsync(
                    modal,
                    JsonSerializer.SerializeToElement(input.Text ?? string.Empty),
                    error,
                    interactiveControls);
                panel.Children.Add(input);
                panel.Children.Add(submit);
                break;
            }
            case MultipleChoiceModalContent choices when choices.AllowsMultiple:
            {
                var selections = new List<(CheckBox CheckBox, JsonElement Value)>();
                foreach (var option in choices.Options)
                {
                    var checkBox = new CheckBox { Content = FormatOption(option) };
                    AutomationProperties.SetName(checkBox, $"Select {FormatOption(option)}");
                    selections.Add((checkBox, option.Clone()));
                    interactiveControls.Add(checkBox);
                    panel.Children.Add(checkBox);
                }
                var submit = this.CreateButton("Submit selected responses", "Submit");
                interactiveControls.Add(submit);
                submit.Click += async (_, _) => await this.RespondAsync(
                    modal,
                    JsonSerializer.SerializeToElement(
                        selections.Where(selection => selection.CheckBox.IsChecked == true)
                            .Select(selection => selection.Value)
                            .ToArray()),
                    error,
                    interactiveControls);
                panel.Children.Add(submit);
                break;
            }
            case MultipleChoiceModalContent choices:
                foreach (var option in choices.Options)
                {
                    var captured = option.Clone();
                    var label = FormatOption(captured);
                    var button = this.CreateButton($"Choose {label}", label);
                    interactiveControls.Add(button);
                    button.Click += async (_, _) => await this.RespondAsync(
                        modal,
                        captured,
                        error,
                        interactiveControls);
                    panel.Children.Add(button);
                }
                break;
            case ApprovalModalContent approval:
            {
                var buttons = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                };
                var approve = this.CreateButton(approval.ApproveLabel, approval.ApproveLabel);
                var reject = this.CreateButton(approval.RejectLabel, approval.RejectLabel);
                interactiveControls.Add(approve);
                interactiveControls.Add(reject);
                approve.Click += async (_, _) => await this.RespondAsync(
                    modal,
                    JsonSerializer.SerializeToElement(true),
                    error,
                    interactiveControls);
                reject.Click += async (_, _) => await this.RespondAsync(
                    modal,
                    JsonSerializer.SerializeToElement(false),
                    error,
                    interactiveControls);
                buttons.Children.Add(approve);
                buttons.Children.Add(reject);
                panel.Children.Add(buttons);
                break;
            }
        }
    }

    private Button CreateButton(string automationName, string content)
    {
        var button = new Button { Content = content };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private async Task RespondAsync(
        AgentSessionModalViewModel modal,
        JsonElement response,
        TextBlock error,
        IReadOnlyList<Control> interactiveControls)
    {
        var lifetimeCancellation = this.lifetimeCancellation;
        foreach (var control in interactiveControls)
        {
            control.IsEnabled = false;
        }
        error.IsVisible = false;
        try
        {
            await modal.RespondAsync(response, lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            error.Text = "The response was cancelled.";
            error.IsVisible = true;
            this.Enable(interactiveControls);
        }
        catch (RemoteAgentSessionException)
        {
            this.ShowResponseError(error, interactiveControls);
        }
        catch (RemoteAgentProtocolException)
        {
            this.ShowResponseError(error, interactiveControls);
        }
        catch (InvalidOperationException)
        {
            this.ShowResponseError(error, interactiveControls);
        }
        catch (ArgumentException)
        {
            this.ShowResponseError(error, interactiveControls);
        }
    }

    private void ShowResponseError(TextBlock error, IReadOnlyList<Control> controls)
    {
        error.Text = "The response could not be sent. Try again.";
        error.IsVisible = true;
        this.Enable(controls);
    }

    private void Enable(IReadOnlyList<Control> controls)
    {
        foreach (var control in controls)
        {
            control.IsEnabled = true;
        }
    }

    private static string FormatOption(JsonElement option) =>
        option.ValueKind == JsonValueKind.String
            ? option.GetString() ?? string.Empty
            : option.GetRawText();
}
