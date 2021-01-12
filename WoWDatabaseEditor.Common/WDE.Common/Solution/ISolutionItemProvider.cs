﻿using System.Windows.Media;
using WDE.Module.Attributes;

namespace WDE.Common
{
    [NonUniqueProvider]
    public interface ISolutionItemProvider
    {
        string GetName();
        ImageSource GetImage();
        string GetDescription();

        ISolutionItem CreateSolutionItem();
    }
}