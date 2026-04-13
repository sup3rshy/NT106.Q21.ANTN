using System.Windows.Controls;
using NetDraw.Shared.Models;

namespace NetDraw.Client.Services;

public interface IFileService
{
    void Save(string path, List<DrawActionBase> actions);
    List<DrawActionBase>? Load(string path);
    void ExportPng(Canvas canvas, string path);
}
