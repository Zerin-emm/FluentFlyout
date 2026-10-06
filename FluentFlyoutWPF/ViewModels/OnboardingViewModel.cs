// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFlyout.Classes.Settings;
using System.Windows;

namespace FluentFlyoutWPF.ViewModels;

public sealed class OnboardingStep
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string ImageSource { get; init; }
}

public class OnboardingViewModel : ObservableObject
{
    private int _currentStepIndex;
    private bool _isLoading = true;
    private bool _isTransitioning;

    public UserSettings Settings => SettingsManager.Current;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsTransitioning
    {
        get => _isTransitioning;
        set
        {
            if (SetProperty(ref _isTransitioning, value))
            {
                GoBackCommand.NotifyCanExecuteChanged();
                GoNextCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<OnboardingStep> Steps { get; } =
    [
        new OnboardingStep
        {
            Title = ResolveString("MediaFlyoutTitle"),
            Description = ResolveString("MediaFlyoutDescription"),
            ImageSource = "/Resources/Onboarding/MediaFlyout.png"
        },
        new OnboardingStep
        {
            Title = ResolveString("VolumeFlyoutTitle"),
            Description = ResolveString("VolumeFlyoutDescription"),
            ImageSource = "/Resources/FluentFlyoutVolumeDemo.png"
        },
        new OnboardingStep
        {
            Title = ResolveString("LockKeysCustomizationTitle"),
            Description = ResolveString("LockKeysDescription"),
            ImageSource = "/Resources/Onboarding/LockKeysFlyout.png"
        },
        new OnboardingStep
        {
            Title = ResolveString("UnlockFullExperienceText"),
            Description = "",
            ImageSource = "/Resources/Onboarding/Taskbar.png"
        }
    ];

    /// <summary>
    /// Resolves a localized resource without throwing when the key or the application resources are missing
    /// </summary>
    /// <remarks>
    /// The steps above are built by a property initializer, so a missing key used to surface as a
    /// NullReferenceException while the window was being constructed - before any error handling exists.
    /// </remarks>
    private static string ResolveString(string key)
    {
        return Application.Current?.TryFindResource(key)?.ToString() ?? string.Empty;
    }

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            if (SetProperty(ref _currentStepIndex, value))
            {
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(StepProgressText));
                OnPropertyChanged(nameof(NextButtonText));
                OnPropertyChanged(nameof(IsLastStep));
                OnPropertyChanged(nameof(IsMediaStep));
                OnPropertyChanged(nameof(IsVolumeStep));
                OnPropertyChanged(nameof(IsLockKeysStep));
                OnPropertyChanged(nameof(IsPremiumStep));
                OnPropertyChanged(nameof(CanGoBack));
                GoBackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public OnboardingStep CurrentStep => Steps[CurrentStepIndex];

    public string StepProgressText => string.Format(ResolveString("OnboardingStepsCounter"), CurrentStepIndex + 1, Steps.Count);

    public string? NextButtonText => IsLastStep ? ResolveString("Finish") : ResolveString("Next");

    public bool IsLastStep => CurrentStepIndex >= Steps.Count - 1;

    public bool IsMediaStep => CurrentStepIndex == 0;

    public bool IsVolumeStep => CurrentStepIndex == 1;

    public bool IsLockKeysStep => CurrentStepIndex == 2;

    public bool IsPremiumStep => CurrentStepIndex == 3;

    public bool CanGoBack => CurrentStepIndex > 0;

    public event EventHandler? Completed;

    public IRelayCommand GoBackCommand { get; }

    public IRelayCommand GoNextCommand { get; }

    public IRelayCommand SkipCommand { get; }

    public OnboardingViewModel()
    {
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack && !IsTransitioning);
        GoNextCommand = new RelayCommand(GoNext, () => !IsTransitioning);
        SkipCommand = new RelayCommand(Skip);
    }

    private void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        CurrentStepIndex--;
    }

    private void GoNext()
    {
        if (IsLastStep)
        {
            SettingsManager.SaveSettings();
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        CurrentStepIndex++;
    }

    private void Skip()
    {
        SettingsManager.SaveSettings();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}