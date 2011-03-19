﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Web.Configuration;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Core;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.Utils;
using DevExpress.Persistent.Base;
using Xpand.ExpressApp.Core;

namespace Xpand.ExpressApp.ModelDifference.Core {
    public class ApplicationBuilder {
        Func<string, ITypesInfo> _buildTypesInfoSystem = BuildTypesInfoSystem();
        string _moduleName;
        string _assembliesPath;

        ApplicationBuilder() {
        }

        public static ApplicationBuilder Create() {
            return new ApplicationBuilder();
        }

        static Func<string, ITypesInfo> BuildTypesInfoSystem() {
            return moduleName => TypesInfoBuilder.Create()
                                     .FromModule(moduleName)
                                     .Build();
        }
        public ApplicationBuilder UsingTypesInfo(Func<string,ITypesInfo> buildTypesInfoSystem) {
            _buildTypesInfoSystem = buildTypesInfoSystem;
            return this;
        }

        public ApplicationBuilder FromModule(string moduleName) {
            _moduleName = moduleName;
            return this;
        }

        public XafApplication Build() {
            string assemblyPath = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            try {
                var typesInfo = _buildTypesInfoSystem.Invoke(_moduleName);
                ReflectionHelper.AddResolvePath(assemblyPath);
                var assembly = ReflectionHelper.GetAssembly(Path.GetFileNameWithoutExtension(_moduleName), assemblyPath);
                var assemblyInfo = typesInfo.FindAssemblyInfo(assembly);
                typesInfo.LoadTypes(assembly);
                var findTypeInfo = typesInfo.FindTypeInfo(typeof(XafApplication));
                var findTypeDescendants = ReflectionHelper.FindTypeDescendants(assemblyInfo, findTypeInfo, false);
                var xafApplication = ((XafApplication) Enumerator.GetFirst(findTypeDescendants).CreateInstance(new object[0]));
                if (ConfigurationManager.ConnectionStrings["ConnectionString"] != null) {
                    ((ISupportFullConnectionString)xafApplication).ConnectionString = ConfigurationManager.ConnectionStrings["ConnectionString"].ConnectionString;
                }
                XpandModuleBase.DisposeManagers();
                string config = GetConfigPath();
                var modulesManager = CreateModulesManager(xafApplication, config, _assembliesPath, typesInfo);
                BuildModel(xafApplication, config, modulesManager);
                XpandModuleBase.ReStoreManagers();
                return xafApplication;
            } finally {
                ReflectionHelper.RemoveResolvePath(assemblyPath);
            }
        }
        void BuildModel(XafApplication application, string configFileName, ApplicationModulesManager applicationModulesManager) {
            var modelsManager = new ApplicationModelsManager(applicationModulesManager.Modules, applicationModulesManager.ControllersManager, applicationModulesManager.DomainComponents, application.ResourcesExportedToModel, GetAspects(configFileName));
            var modelApplication = (ModelApplicationBase)modelsManager.CreateModel(modelsManager.CreateApplicationCreator(), null, false);
            var modelApplicationBase = modelApplication.CreatorInstance.CreateModelApplication();
            modelApplicationBase.Id = "After Setup";
            modelApplication.AddLayer(modelApplicationBase);
            return;
        }
        private string[] GetModulesFromConfig(XafApplication application) {
            Configuration config;
            if (application is IWinApplication) {
                config = ConfigurationManager.OpenExeConfiguration(AppDomain.CurrentDomain.SetupInformation.ApplicationBase + _moduleName);
            } else {
                var mapping = new WebConfigurationFileMap();
                mapping.VirtualDirectories.Add("/Dummy", new VirtualDirectoryMapping(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, true));
                config = WebConfigurationManager.OpenMappedWebConfiguration(mapping, "/Dummy");
            }

            if (config.AppSettings.Settings["Modules"] != null) {
                return config.AppSettings.Settings["Modules"].Value.Split(';');
            }

            return null;
        }
        private IEnumerable<string> GetAspects(string configFileName) {
            if (!string.IsNullOrEmpty(configFileName) && configFileName.EndsWith(".config")) {
                var exeConfigurationFileMap = new ExeConfigurationFileMap { ExeConfigFilename = configFileName };
                Configuration configuration = ConfigurationManager.OpenMappedExeConfiguration(exeConfigurationFileMap, ConfigurationUserLevel.None);
                KeyValueConfigurationElement languagesElement = configuration.AppSettings.Settings["Languages"];
                if (languagesElement != null) {
                    string languages = languagesElement.Value;
                    return languages.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            return null;
        }

        ApplicationModulesManager CreateModulesManager(XafApplication application, string configFileName, string assembliesPath, ITypesInfo typesInfo) {
            if (!string.IsNullOrEmpty(configFileName)) {
                bool isWebApplicationModel = string.Compare(Path.GetFileNameWithoutExtension(configFileName), "web", true) == 0;
                if (string.IsNullOrEmpty(assembliesPath)) {
                    assembliesPath = Path.GetDirectoryName(configFileName);
                    if (isWebApplicationModel) {
                        assembliesPath = Path.Combine(assembliesPath + "", "Bin");
                    }
                }
            }
            ReflectionHelper.AddResolvePath(assembliesPath);
            try {
                var result = new ApplicationModulesManager(new ControllersManager(), assembliesPath);
                if (application != null) {
                    foreach (ModuleBase module in application.Modules) {
                        result.AddModule(module);
                    }
                    result.Security = application.Security;
                }
                if (!string.IsNullOrEmpty(configFileName)) {
                    result.AddModuleFromAssemblies(GetModulesFromConfig(application));
                }
                if (typesInfo is TypesInfoBuilder.TypesInfo)
                    XpandModuleBase.Dictiorary = ((TypesInfoBuilder.TypesInfo)typesInfo).Source.XPDictionary;
                XpandModuleBase.TypesInfo = typesInfo;
                result.Load(typesInfo, typesInfo != XafTypesInfo.Instance);
                return result;
            } finally {
                XpandModuleBase.Dictiorary = XafTypesInfo.XpoTypeInfoSource.XPDictionary;
                XpandModuleBase.TypesInfo = XafTypesInfo.Instance;
                ReflectionHelper.RemoveResolvePath(assembliesPath);
            }

        }

        string GetConfigPath() {
            string path = Path.Combine(_assembliesPath, _moduleName);
            string config = path + ".config";
            if (File.Exists(_assembliesPath + "web.config"))
                config = Path.Combine(_assembliesPath, "web.config");
            return config;
        }

        public ApplicationBuilder WithAssembliesPath(string assembliesPath) {
            _assembliesPath = assembliesPath;
            return this;
        }
    }

    public class TypesInfoBuilder {
        string _moduleName;

        public static TypesInfoBuilder Create() {
            return new TypesInfoBuilder();
        }

        public TypesInfoBuilder FromModule(string moduleName) {
            _moduleName = moduleName;
            return this;
        }

        public ITypesInfo Build() {
            return _moduleName == Assembly.GetAssembly(XpandModuleBase.Application.GetType()).ManifestModule.Name
                       ? XafTypesInfo.Instance
                       : GetTypesInfo();
        }

        TypesInfo GetTypesInfo() {
            var typesInfo = new TypesInfo();
            var xpoSource = new XpoTypeInfoSource(typesInfo);
            typesInfo.Source = xpoSource;
            typesInfo.AddSource(new ReflectionTypeInfoSource());
            typesInfo.AddSource(xpoSource);
            typesInfo.AddSource(new DynamicTypeInfoSource());
            typesInfo.SetRedirectStrategy((@from, info) => xpoSource.GetFirstRegisteredTypeForEntity(from) ?? from);
            XpandModuleBase.Dictiorary = xpoSource.XPDictionary;
            return typesInfo;
        }

        public class TypesInfo : DevExpress.ExpressApp.DC.TypesInfo {
            public DevExpress.ExpressApp.DC.Xpo.XpoTypeInfoSource Source { get; set; }
        }

    }
    public class ModelLoader {
        readonly string _executableName;

        public ModelLoader(string executableName) {
            _executableName = executableName;
        }

        public ModelApplicationBase GetMasterModel() {
            var xafApplication =ApplicationBuilder.Create().
                FromModule(_executableName).
                WithAssembliesPath(AppDomain.CurrentDomain.SetupInformation.ApplicationBase).
                Build();
            return (ModelApplicationBase) xafApplication.Model;
        }

        public ModelApplicationBase GetLayer(Type modelApplicationFromStreamStoreBaseType) {
            var masterModel = GetMasterModel();
            var layer = masterModel.CreatorInstance.CreateModelApplication();

            masterModel.AddLayerBeforeLast(layer);
            var storeBase = (ModelApplicationFromStreamStoreBase)ReflectionHelper.CreateObject(modelApplicationFromStreamStoreBaseType);
            storeBase.Load(layer);
            return layer;
        }


    }
}
