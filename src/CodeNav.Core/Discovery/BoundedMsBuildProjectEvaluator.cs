using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

/// <summary>
/// Language-neutral ordered traversal for the bounded semantic MSBuild model. Language projections
/// decide which properties/items matter and how imports resolve; this class owns document order,
/// ImportGroup/Choose structure, recursion bounds, import-cycle detection, and cancellation.
/// </summary>
internal abstract class BoundedMsBuildProjectEvaluator<TRole, TChooseState>
    where TRole : struct, Enum
{
    private readonly CancellationToken _cancellationToken;
    private readonly int _maxEvaluationDepth;
    private readonly int _maxImportDepth;
    private readonly HashSet<string> _activeImports;
    private int _evaluationDepth;

    protected BoundedMsBuildProjectEvaluator(CancellationToken cancellationToken,
        int maxEvaluationDepth, int maxImportDepth)
    {
        _cancellationToken = cancellationToken;
        _maxEvaluationDepth = maxEvaluationDepth;
        _maxImportDepth = maxImportDepth;
        _activeImports = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
    }

    protected abstract bool HasEvaluationError { get; }
    protected abstract void SetEvaluationError(string cause);
    protected abstract bool ShouldProcessElement(XElement element, string documentPath,
        out bool process);
    protected abstract bool ValidateContainer(XElement container);
    protected abstract void ProcessPropertyGroupElement(XElement group, string documentPath,
        TRole role);
    protected abstract void ProcessItemGroupElement(XElement group, string documentPath,
        TRole role);
    protected abstract void ProcessImportElement(XElement import, string documentPath,
        TRole role, int depth);
    protected abstract void ProcessOtherElement(XElement element, string documentPath,
        TRole role, int depth);
    protected abstract bool TryReserveImportOccurrence();
    protected abstract bool TryResolveImportRootCore(string importPath, out XElement? root);
    protected abstract bool ValidateImportedRoot(XElement root);
    protected abstract TChooseState CaptureChooseState(XElement choose);
    protected abstract void OnChooseSkipped(XElement choose, TChooseState state);
    protected abstract void OnChooseCompleted(XElement choose, TChooseState state);

    protected void CheckEvaluationCancellation() =>
        _cancellationToken.ThrowIfCancellationRequested();

    protected void ProcessEvaluationContainer(XElement container, string documentPath,
        TRole role, int depth)
    {
        CheckEvaluationCancellation();
        if (HasEvaluationError) return;
        if (++_evaluationDepth > _maxEvaluationDepth)
        {
            _evaluationDepth--;
            SetEvaluationError("evaluation_depth_limit");
            return;
        }
        try
        {
            if (!ShouldProcessElement(container, documentPath, out bool process) || !process)
                return;
            if (!ValidateContainer(container)) return;

            foreach (XElement child in container.Elements())
            {
                CheckEvaluationCancellation();
                if (HasEvaluationError) return;
                switch (child.Name.LocalName)
                {
                    case "PropertyGroup":
                        ProcessPropertyGroupElement(child, documentPath, role);
                        break;
                    case "ItemGroup":
                        ProcessItemGroupElement(child, documentPath, role);
                        break;
                    case "Import":
                        ProcessImportElement(child, documentPath, role, depth);
                        break;
                    case "ImportGroup":
                        ProcessImportGroup(child, documentPath, role, depth);
                        break;
                    case "Choose":
                        ProcessChoose(child, documentPath, role, depth);
                        break;
                    default:
                        ProcessOtherElement(child, documentPath, role, depth);
                        break;
                }
            }
        }
        finally
        {
            _evaluationDepth--;
        }
    }

    protected void ProcessResolvedEvaluationImport(string importPath, TRole role, int depth)
    {
        if (!TryReserveImportOccurrence())
        {
            SetEvaluationError("import_occurrence_limit");
            return;
        }
        if (depth >= _maxImportDepth)
        {
            SetEvaluationError("import_depth_limit");
            return;
        }
        if (!_activeImports.Add(importPath))
        {
            SetEvaluationError("import_cycle");
            return;
        }
        try
        {
            if (!TryResolveImportRootCore(importPath, out XElement? importedRoot) ||
                importedRoot is null) return;
            if (!ValidateImportedRoot(importedRoot)) return;
            ProcessEvaluationContainer(importedRoot, importPath, role, depth + 1);
        }
        finally
        {
            _activeImports.Remove(importPath);
        }
    }

    private void ProcessImportGroup(XElement group, string documentPath, TRole role, int depth)
    {
        CheckEvaluationCancellation();
        if (!ShouldProcessElement(group, documentPath, out bool process) || !process) return;
        foreach (XElement child in group.Elements())
        {
            CheckEvaluationCancellation();
            if (child.Name.LocalName != "Import")
            {
                SetEvaluationError("import_unsupported");
                return;
            }
            ProcessImportElement(child, documentPath, role, depth);
            if (HasEvaluationError) return;
        }
    }

    private void ProcessChoose(XElement choose, string documentPath, TRole role, int depth)
    {
        CheckEvaluationCancellation();
        TChooseState state = CaptureChooseState(choose);
        if (!ShouldProcessElement(choose, documentPath, out bool process)) return;
        if (!process)
        {
            OnChooseSkipped(choose, state);
            return;
        }

        XElement? otherwise = null;
        foreach (XElement branch in choose.Elements())
        {
            CheckEvaluationCancellation();
            if (branch.Name.LocalName == "Otherwise")
            {
                if (otherwise is not null)
                {
                    SetEvaluationError("condition_unsupported");
                    return;
                }
                otherwise = branch;
                continue;
            }
            if (branch.Name.LocalName != "When" || branch.Attribute("Condition") is null)
            {
                SetEvaluationError("condition_unsupported");
                return;
            }
            if (!ShouldProcessElement(branch, documentPath, out bool selected)) return;
            if (!selected) continue;
            ProcessEvaluationContainer(branch, documentPath, role, depth);
            if (!HasEvaluationError) OnChooseCompleted(choose, state);
            return;
        }
        if (otherwise is not null)
            ProcessEvaluationContainer(otherwise, documentPath, role, depth);
        if (!HasEvaluationError) OnChooseCompleted(choose, state);
    }
}
