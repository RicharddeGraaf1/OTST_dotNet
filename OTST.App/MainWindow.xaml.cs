using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using OTST.Application.Services;
using OTST.Domain.Models;
using OTST.Domain.Services;
using OTST.Domain.Services.Doorlevering;
using OTST.Domain.Services.Validation;

namespace OTST.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ZipAnalysisFacade _analysisFacade = new();
    private readonly IntrekkingTransformationFacade _intrekkingFacade = new();
    private readonly ValidationTransformationFacade _validationFacade = new();
    private readonly DoorleveringTransformationFacade _doorleveringFacade = new();

    public MainWindow()
    {
        InitializeComponent();
        SetTransformationButtonsEnabled(false);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ZIP-bestanden (*.zip)|*.zip|Alle bestanden (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SourcePathTextBox.Text = dialog.FileName;
            SetTransformationButtonsEnabled(true);
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSourcePath(out var path))
        {
            return;
        }

        SetBusyState(true, "Bezig met analyseren...");
        try
        {
            var result = await _analysisFacade.AnalyseAsync(path);
            ResultTextBox.Text = BuildAnalysisReport(path, result);
            StatusTextBlock.Text = $"Analyse voltooid om {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Analyse is mislukt.";
            ResultTextBox.Text = string.Empty;
            MessageBox.Show(this, ex.Message, "Fout tijdens analyse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool isBusy, string? status = null)
    {
        AnalyzeButton.IsEnabled = !isBusy;
        SourcePathTextBox.IsEnabled = !isBusy;
        SetTransformationButtonsEnabled(!isBusy);

        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusTextBlock.Text = status;
        }
    }

    private static string BuildAnalysisReport(string path, ZipAnalysisResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Bronbestand: {path}");
        builder.AppendLine($"FRBR Work: {result.FrbrWork ?? "n.v.t."}");
        builder.AppendLine($"FRBR Expression: {result.FrbrExpression ?? "n.v.t."}");
        builder.AppendLine($"Doel: {result.Doel ?? "n.v.t."}");
        builder.AppendLine($"Bevoegd gezag: {result.BevoegdGezag ?? "n.v.t."}");
        builder.AppendLine($"Aantal informatieobjecten: {result.AantalInformatieObjecten}");
        builder.AppendLine($"Totale GML-grootte: {FormatSize(result.TotaleGmlBestandsgrootte)}");
        builder.AppendLine();
        builder.AppendLine("Informatieobjecten:");
        builder.AppendLine("-------------------");

        foreach (var io in result.InformatieObjecten)
        {
            builder.AppendLine($"- Map: {io.Folder}");
            builder.AppendLine($"  FRBR Work: {io.FrbrWork ?? "n.v.t."}");
            builder.AppendLine($"  FRBR Expression: {io.FrbrExpression ?? "n.v.t."}");
            builder.AppendLine($"  ExtIoRef eId: {io.ExtIoRefEId ?? "n.v.t."}");
            builder.AppendLine($"  Bestandsnaam: {io.Bestandsnaam ?? "n.v.t."}");
            builder.AppendLine($"  Bestandhash: {io.BestandHash ?? "n.v.t."}");
            builder.AppendLine($"  Officiële titel: {io.OfficieleTitel ?? "n.v.t."}");
            builder.AppendLine();
        }

        if (result.ExtIoRefs.Count > 0)
        {
            builder.AppendLine("ExtIoRef-koppelingen:");
            builder.AppendLine("----------------------");
            foreach (var ext in result.ExtIoRefs)
            {
                builder.AppendLine($"- ref: {ext.Ref}, eId: {ext.EId ?? "n.v.t."}");
            }
        }

        return builder.ToString();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024d * 1024):0.##} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    private bool TryGetSourcePath(out string path)
    {
        path = SourcePathTextBox.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Selecteer eerst een bronbestand.", "OTST", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Bestand niet gevonden:{Environment.NewLine}{path}", "OTST", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SetTransformationButtonsEnabled(bool isEnabled)
    {
        PublicatieButton.IsEnabled = isEnabled;
        ValidatieButton.IsEnabled = isEnabled;
        IntrekkingPublicatieButton.IsEnabled = isEnabled;
        IntrekkingValidatieButton.IsEnabled = isEnabled;
        DoorleverenButton.IsEnabled = isEnabled;
        ValidatieDoorleverenButton.IsEnabled = isEnabled;
    }

    private void HandlePublicatie_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteValidationAsync(isValidation: false);

    private void HandleValidatie_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteValidationAsync(isValidation: true);

    private void HandleIntrekkingPublicatie_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteIntrekkingPublicatieAsync();

    private void HandleIntrekkingValidatie_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteIntrekkingValidatieAsync();

    private void HandleDoorleveren_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteDoorleveringAsync(isValidation: false);

    private void HandleValidatieDoorleveren_Click(object sender, RoutedEventArgs e) =>
        _ = ExecuteDoorleveringAsync(isValidation: true);
    private async Task ExecuteValidationAsync(bool isValidation)
    {
        if (!TryGetSourcePath(out var path))
        {
            return;
        }

        var outputPath = ValidationTransformationService.GetDefaultOutputPath(path, isValidation);
        var label = isValidation ? "Validatie" : "Publicatie";
        SetBusyState(true, $"Bezig met {label.ToLowerInvariant()}...");
        try
        {
            var result = await Task.Run(() =>
                isValidation
                    ? _validationFacade.TransformValidation(path, outputPath)
                    : _validationFacade.TransformPublicatie(path, outputPath));

            ResultTextBox.Text = await File.ReadAllTextAsync(result.ReportPath, Encoding.UTF8);
            StatusTextBlock.Text = $"{label} voltooid. Output: {result.OutputZipPath}";

            MessageBox.Show(this,
                $"{label} is voltooid.{Environment.NewLine}Output: {result.OutputZipPath}{Environment.NewLine}Rapport: {result.ReportPath}",
                label,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"{label} mislukt.";
            MessageBox.Show(this,
                ex.Message,
                $"Fout tijdens {label.ToLowerInvariant()}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }


    private void ShowFeatureInProgress(string featureName)
    {
        MessageBox.Show(this,
            $"De functionaliteit voor {featureName} wordt momenteel geïmplementeerd.\n" +
            $"Een volgende versie van OTST (.NET) zal deze actie ondersteunen.",
            "Nog niet beschikbaar",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task ExecuteIntrekkingValidatieAsync()
    {
        if (!TryGetSourcePath(out var path))
        {
            return;
        }

        var outputPath = IntrekkingTransformationService.GetDefaultOutputPath(path, isValidation: true);
        var reportPath = IntrekkingTransformationService.GetReportPath(outputPath);

        SetBusyState(true, "Bezig met intrekking validatie...");
        try
        {
            var result = await _intrekkingFacade.TransformIntrekkingValidatieAsync(path, outputPath);
            ResultTextBox.Text = await File.ReadAllTextAsync(result.ReportPath, Encoding.UTF8);
            StatusTextBlock.Text = $"Intrekking validatie voltooid. Output: {result.OutputZipPath}";

            MessageBox.Show(this,
                $"Intrekking validatie is voltooid.{Environment.NewLine}Output: {result.OutputZipPath}{Environment.NewLine}Rapport: {result.ReportPath}",
                "Intrekking validatie",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Intrekking validatie mislukt.";
            MessageBox.Show(this, ex.Message, "Fout tijdens intrekking validatie", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private async Task ExecuteIntrekkingPublicatieAsync()
    {
        if (!TryGetSourcePath(out var path))
        {
            return;
        }

        var outputPath = IntrekkingTransformationService.GetDefaultOutputPath(path, isValidation: false);

        SetBusyState(true, "Bezig met intrekking publicatie...");
        try
        {
            var result = await _intrekkingFacade.TransformIntrekkingPublicatieAsync(path, outputPath);
            ResultTextBox.Text = await File.ReadAllTextAsync(result.ReportPath, Encoding.UTF8);
            StatusTextBlock.Text = $"Intrekking publicatie voltooid. Output: {result.OutputZipPath}";

            MessageBox.Show(this,
                $"Intrekking publicatie is voltooid.{Environment.NewLine}Output: {result.OutputZipPath}{Environment.NewLine}Rapport: {result.ReportPath}",
                "Intrekking publicatie",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Intrekking publicatie mislukt.";
            MessageBox.Show(this, ex.Message, "Fout tijdens intrekking publicatie", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private async Task ExecuteDoorleveringAsync(bool isValidation)
    {
        if (!TryGetSourcePath(out var path))
        {
            return;
        }

        var outputPath = DoorleveringTransformationService.GetDefaultOutputPath(path, isValidation);
        var label = isValidation ? "Validatie doorlevering" : "Doorlevering";
        SetBusyState(true, $"Bezig met {label.ToLowerInvariant()}...");
        try
        {
            var result = await Task.Run(() =>
                isValidation
                    ? _doorleveringFacade.TransformValidatieDoorlevering(path, outputPath)
                    : _doorleveringFacade.TransformDoorlevering(path, outputPath));

            ResultTextBox.Text = await File.ReadAllTextAsync(result.ReportPath, Encoding.UTF8);
            StatusTextBlock.Text = $"{label} voltooid. Output: {result.OutputZipPath}";

            MessageBox.Show(this,
                $"{label} is voltooid.{Environment.NewLine}Output: {result.OutputZipPath}{Environment.NewLine}Rapport: {result.ReportPath}",
                label,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"{label} mislukt.";
            MessageBox.Show(this,
                ex.Message,
                $"Fout tijdens {label.ToLowerInvariant()}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusyState(false);
        }
    }
}