﻿using WDE.Module.Attributes;

namespace WDE.Common.Services
{
    [UniqueProvider]
    public interface IConfigureService
    {
        void ShowSettings();
    }
}