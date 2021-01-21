using System;
using System.Linq;
using System.Text;
using UnityEngine;
using KSP.IO;
using KSP;
using System.IO;

using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KRnD
{
    [KSPAddon(KSPAddon.Startup.SpaceCentre, true)]
    public class KRnD : UnityEngine.MonoBehaviour
    {
        private static bool initialized = false;
        public static Dictionary<string, PartStats> originalStats = null;
        public static Dictionary<string, KRnDUpgrade> upgrades = new Dictionary<string, KRnDUpgrade>();
        public static List<string> fuelResources = null;
        public static List<string> blacklistedParts = null;

        string BLACKLIST_FILE { get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/../PluginData/blacklist.cfg"; } }

        // Helper for accessing values in third party modules:
        public static double getGenericModuleValue(PartModule module, String fieldName)
        {
            Type type = module.GetType();
            foreach (FieldInfo info in type.GetFields())
            {
                if (info.Name == fieldName) return Convert.ToDouble(info.GetValue(module));
            }
            throw new Exception("field " + fieldName + " not found in module " + module.moduleName);
        }

        // Helper for setting values in third party modules:
        public static void setGenericModuleValue(PartModule module, String fieldName, double value)
        {
            Type type = module.GetType();
            foreach (FieldInfo info in type.GetFields())
            {
                if (info.Name == fieldName)
                {
                    info.SetValue(module, Convert.ChangeType(value, info.FieldType));
                    return;
                }
            }
            throw new Exception("field " + fieldName + " not found in module " + module.moduleName);
        }

        // Checks if the given, generic part-module has a field with the given name:
        public static bool hasGenericModuleField(PartModule module, String fieldName)
        {
            Type type = module.GetType();
            foreach (FieldInfo info in type.GetFields())
            {
                if (info.Name == fieldName) return true;
            }
            return false;
        }

        public static KRnDModule getKRnDModule(Part part)
        {
            // If this is a blacklisted part, don't touch it, even if it should have an RnD-Module. We do it like
            // this because using module-manager-magic to prevent RnD from getting installed with other, incompatible
            // modules from other mods depends on the order in which module-manager applies the patches; this way
            // we can avoid these problems. It means though that parts might have the RnD-Module, wich isn't used though.
            if (KRnD.blacklistedParts.Contains(KRnD.sanatizePartName(part.name))) return null;

            // Check if this part has the RnD-Module and return it:
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];
                if (partModule.moduleName == "KRnDModule") return (KRnDModule)partModule;
            }
            return null;
        }

        // Parts can have multiple variants which we return as a list, needed
        // because some variants change the part mass
        public static Dictionary<string, KRnDVariant> getVariants(Part part)
        {
            if (part == null) return null;
            if (part.partInfo.partPrefab == null) return null;

            if (part.partInfo.Variants == null)
            {
                Log.Info("Part: " + part.partInfo.title + ", " + part.partInfo.name +" has no variants");
                return null;
            }
            Log.Info("Part: " + part.partInfo.title + ", " + part.partInfo.name + " has " + part.partInfo.Variants.Count + " variants");

            Dictionary<string, KRnDVariant> variants = new Dictionary<string, KRnDVariant>();

            for (int i = 0; i < part.partInfo.Variants.Count; i++)
            {
                var partVariant = part.partInfo.Variants[i];

                if (!variants.ContainsKey(partVariant.Name))
                {
                    KRnDVariant v = new KRnDVariant(partVariant.Name, partVariant.Mass);
                    variants.Add(partVariant.Name, v);
                }
            }
            return variants;
        }

        static void UpdatePartVariantMasses(Part part, PartStats originalStats, float dryMassFactor)
        {
            if (originalStats.kRnDVariants == null)
                originalStats.kRnDVariants = new Dictionary<string, KRnDVariant>();

            for (int i = 0; i < part.partInfo.Variants.Count; i++)
            {
                var partVariant = part.partInfo.Variants[i];
                if (originalStats.kRnDVariants.TryGetValue(partVariant.Name, out KRnDVariant v) == false)
                {
                    v = new KRnDVariant(partVariant.Name, partVariant.Mass);
                    originalStats.kRnDVariants.Add(partVariant.Name, v);
                }
                v.UpdateMass(dryMassFactor);
                
                originalStats.kRnDVariants[partVariant.Name] = v;
                partVariant.Mass = v.mass;
            }
        }

        // Multi-Mode engines have multiple Engine-Modules which we return as a list.
        public static List<ModuleEngines> getEngineModules(Part part)
        {
            List<ModuleEngines> engines = new List<ModuleEngines>();
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleEngines" || partModule.moduleName == "ModuleEnginesFX")
                {
                    engines.Add((ModuleEngines)partModule);
                }
            }
            if (engines.Count > 0) return engines;
            return null;
        }

        public static ModuleRCS getRcsModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleRCS" || partModule.moduleName == "ModuleRCSFX") return (ModuleRCS)partModule;
            }
            return null;
        }

        public static ModuleReactionWheel getReactionWheelModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleReactionWheel") return (ModuleReactionWheel)partModule;
            }
            return null;
        }

        // WIP for upgrading antenna range. Need to figure out how to get Antenna Rating to display properly in GUI
        //public static ModuleDataTransmitter getDataTransmitterModule(Part part)
        //{
        //    for (int i = 0; i < part.Modules.Count; i++)
        //    {
        //        PartModule partModule = part.Modules[i];

        //        if (partModule.moduleName == "ModuleDataTransmitter") return (ModuleDataTransmitter)partModule;
        //    }
        //    return null;
        //}

        public static ModuleResourceHarvester getResourceHarvesterModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleResourceHarvester") return (ModuleResourceHarvester)partModule;
            }
            return null;
        }

        public static ModuleActiveRadiator getActiveRadiatorModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleActiveRadiator") return (ModuleActiveRadiator)partModule;
            }
            return null;
        }

        public static ModuleDeployableSolarPanel getSolarPanelModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleDeployableSolarPanel") return (ModuleDeployableSolarPanel)partModule;
            }
            return null;
        }

        public static ModuleWheelBase getLandingLegModule(Part part)
        {
            ModuleWheelBase wheelBase = null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleWheelBase")
                {
                    wheelBase = (ModuleWheelBase)partModule;
                    if (wheelBase.wheelType == WheelType.LEG) return wheelBase;
                }
            }
            return null;
        }

        public static PartResource getChargeResource(Part part)
        {
            if (part.Resources == null || part.partInfo.title == "") return null;
            for (int i = 0; i < part.Resources.Count; i++)
            {
                PartResource partResource = part.Resources[i];
                // Engines with an alternator might have a max-amount of 0, skip thoses:
                if (partResource.resourceName == "ElectricCharge" && partResource.maxAmount > 0) return partResource;
            }
            return null;
        }

        public static List<PartResource> getFuelResources(Part part)
        {
            if (part.Resources == null || part.partInfo.title == "") return null;
            List<PartResource> partFuels = new List<PartResource>();
            for (int i = 0; i < part.Resources.Count; i++)
            {
                PartResource partResource = part.Resources[i];
                if (KRnD.fuelResources != null && KRnD.fuelResources.Contains(partResource.resourceName)) partFuels.Add(partResource);
            }
            if (partFuels.Count == 0) return null;
            return partFuels;
        }

        public static ModuleGenerator getGeneratorModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleGenerator") return (ModuleGenerator)partModule;
            }
            return null;
        }

        public static PartModule getFissionGeneratorModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                // We are only interested in "FissionGenerator" with the tunable attribute "PowerGeneration":
                if (partModule.moduleName == "FissionGenerator" && KRnD.hasGenericModuleField(partModule, "PowerGeneration")) return partModule;
            }
            return null;
        }

        public static List<ModuleResourceConverter> getConverterModules(Part part)
        {
            List<ModuleResourceConverter> converters = new List<ModuleResourceConverter>();
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleResourceConverter") converters.Add((ModuleResourceConverter)partModule);
            }
            if (converters.Count == 0) return null;
            return converters;
        }

        public static ModuleParachute getParachuteModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleParachute") return (ModuleParachute)partModule;
            }
            return null;
        }

        public static ModuleProceduralFairing getFairingModule(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule partModule = part.Modules[i];

                if (partModule.moduleName == "ModuleProceduralFairing") return (ModuleProceduralFairing)partModule;
            }
            return null;
        }

        public static float calculateImprovementFactor(float baseImprovement, float improvementScale, int upgrades)
        {
            float factor = 0;
            if (upgrades < 0) upgrades = 0;
            for (int i = 0; i < upgrades; i++)
            {
                if (i == 0) factor += baseImprovement;
                else factor += baseImprovement * (float)Math.Pow(improvementScale, i - 1);
            }
            if (baseImprovement < 0 && factor < -0.9) factor = -0.9f;
            return (float)Math.Round(factor, 4);
        }

        public static int calculateScienceCost(int baseCost, float costScale, int upgrades)
        {
            float cost = 0;
            if (upgrades < 0) upgrades = 0;
            for (int i = 0; i < upgrades; i++)
            {
                if (i == 0) cost = baseCost;
                else cost += baseCost * (float)Math.Pow(costScale, i - 1);
            }
            if (cost > 2147483647) return 2147483647; // Cap at signed 32 bit int
            return (int)Math.Round(cost);
        }

        // Since KSP 1.1 the info-text of solar panels is not updated correctly, so we have use this workaround-function
        // to create our own text.
        public static String getSolarPanelInfo(ModuleDeployableSolarPanel solarModule)
        {
            String info = solarModule.GetInfo();
            float chargeRate = solarModule.chargeRate * solarModule.efficiencyMult;
            String chargeString = chargeRate.ToString("0.####/s");
            String prefix = "<b>Electric Charge: </b>";
            return Regex.Replace(info, prefix + "[0-9.]+/[A-Za-z.]+", prefix + chargeString);
        }

        // Updates the global dictionary of available parts with the current set of upgrades (should be
        // executed for example when a new game starts or an existing game is loaded).
        public static int updateGlobalParts()
        {
            int upgradesApplied = 0;
            try
            {
                if (KRnD.upgrades == null) throw new Exception("upgrades-dictionary missing");
                for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
                {
                    AvailablePart part = PartLoader.LoadedPartsList[i];

                    try
                    {
                        KRnDUpgrade upgrade;
                        if (!KRnD.upgrades.TryGetValue(part.name, out upgrade)) upgrade = new KRnDUpgrade(); // If there are no upgrades, reset the part.

                        // Udate the part to its latest model:
                        KRnD.updatePart(part.partPrefab, true);

                        // Rebuild the info-screen:
                        int converterModuleNumber = 0; // There might be multiple modules of this type
                        int engineModuleNumber = 0; // There might be multiple modules of this type

                        for (int i2 = 0; i2 < part.moduleInfos.Count; i2++)
                        {
                            AvailablePart.ModuleInfo info = part.moduleInfos[i2];

                            switch (info.moduleName.ToLower())
                            {
                                case "engine":
                                    {
                                        List<ModuleEngines> engines = KRnD.getEngineModules(part.partPrefab);
                                        if (engines != null && engines.Count > 0)
                                        {
                                            ModuleEngines engine = engines[engineModuleNumber];
                                            info.info = engine.GetInfo();
                                            info.primaryInfo = engine.GetPrimaryField();
                                            engineModuleNumber++;
                                        }
                                    }
                                    break;
                                case "rcs":
                                    {
                                        ModuleRCS rcs = KRnD.getRcsModule(part.partPrefab);
                                        if (rcs) info.info = rcs.GetInfo();
                                    }
                                    break;
                                case "reaction wheel":
                                    {
                                        ModuleReactionWheel reactionWheel = KRnD.getReactionWheelModule(part.partPrefab);
                                        if (reactionWheel) info.info = reactionWheel.GetInfo();
                                    }
                                    break;
                                // WIP
                                //case "antenna power":
                                //    {
                                //        ModuleDataTransmitter dataTransmitter = KRnD.getDataTransmitterModule(part.partPrefab);
                                //        if (dataTransmitter) info.info = dataTransmitter.GetInfo();
                                //    }
                                //    break;
                                case "drill effeciency":
                                    {
                                        ModuleResourceHarvester resourceHarvester = KRnD.getResourceHarvesterModule(part.partPrefab);
                                        if (resourceHarvester) info.info = resourceHarvester.GetInfo();
                                    }
                                    break;
                                case "radiator effeciency":
                                    {
                                        ModuleActiveRadiator activeRadiator = KRnD.getActiveRadiatorModule(part.partPrefab);
                                        if (activeRadiator) info.info = activeRadiator.GetInfo();
                                    }
                                    break;
                                case "deployable solar panel":
                                    {
                                        ModuleDeployableSolarPanel solarPanel = KRnD.getSolarPanelModule(part.partPrefab);
                                        if (solarPanel) info.info = KRnD.getSolarPanelInfo(solarPanel);
                                    }
                                    break;
                                case "landing leg":
                                    {
                                        ModuleWheelBase landingLeg = KRnD.getLandingLegModule(part.partPrefab);
                                        if (landingLeg) info.info = landingLeg.GetInfo();
                                    }
                                    break;
                                case "fission generator":
                                    {
                                        PartModule fissionGenerator = KRnD.getFissionGeneratorModule(part.partPrefab);
                                        if (fissionGenerator) info.info = fissionGenerator.GetInfo();
                                    }
                                    break;
                                case "generator":
                                    {
                                        ModuleGenerator generator = KRnD.getGeneratorModule(part.partPrefab);
                                        if (generator) info.info = generator.GetInfo();
                                    }
                                    break;
                                case "resource converter":
                                    {
                                        List<ModuleResourceConverter> converterList = KRnD.getConverterModules(part.partPrefab);
                                        if (converterList != null && converterList.Count > 0)
                                        {
                                            ModuleResourceConverter converter = converterList[converterModuleNumber];
                                            info.info = converter.GetInfo();
                                            converterModuleNumber++;
                                        }
                                    }
                                    break;
                                case "parachute":
                                    {
                                        ModuleParachute parachute = KRnD.getParachuteModule(part.partPrefab);
                                        if (parachute) info.info = parachute.GetInfo();
                                    }
                                    break;
                                case "custom-built fairing":
                                    {
                                        ModuleProceduralFairing fairing = KRnD.getFairingModule(part.partPrefab);
                                        if (fairing) info.info = fairing.GetInfo();
                                    }
                                    break;
                            }
                        }

                        List<PartResource> fuelResources = KRnD.getFuelResources(part.partPrefab);
                        PartResource electricCharge = KRnD.getChargeResource(part.partPrefab);
                        for (int i3 = 0; i3 < part.resourceInfos.Count; i3++)
                        {
                            AvailablePart.ResourceInfo info = part.resourceInfos[i3];

                            // The Resource-Names are not always formated the same way, eg "Electric Charge" vs "ElectricCharge", so we do some reformating.
                            if (electricCharge != null && info.resourceName.Replace(" ", "").ToLower() == electricCharge.resourceName.Replace(" ", "").ToLower())
                            {
                                info.info = electricCharge.GetInfo();
                                info.primaryInfo = "<b>" + info.resourceName + ":</b> " + electricCharge.maxAmount.ToString();
                            }
                            else if (fuelResources != null)
                            {
                                for (int i4 = 0; i4 < fuelResources.Count; i4++)
                                {
                                    PartResource fuelResource = fuelResources[i4];

                                    if (info.resourceName.Replace(" ", "").ToLower() == fuelResource.resourceName.Replace(" ", "").ToLower())
                                    {
                                        info.info = fuelResource.GetInfo();
                                        info.primaryInfo = "<b>" + info.resourceName + ":</b> " + fuelResource.maxAmount.ToString();
                                        break;
                                    }
                                }
                            }
                        }

                        upgradesApplied++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[KRnD] updateGlobalParts(" + part.title.ToString() + "): " + e.ToString());
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] updateGlobalParts(): " + e.ToString());
            }
            return upgradesApplied;
        }

        // Updates all parts in the vessel that is currently active in the editor.
        public static void updateEditorVessel(Part rootPart = null)
        {
            if (rootPart == null) rootPart = EditorLogic.RootPart;
            if (!rootPart) return;
            KRnD.updatePart(rootPart, true); // Update to the latest model
            for (int i = 0; i < rootPart.children.Count; i++)
            {
                Part childPart = rootPart.children[i];

                KRnD.updateEditorVessel(childPart);
            }
        }

        // Updates the given part either to the latest model (updateToLatestModel=TRUE) or to the model defined by its
        // KRnDModule.
        public static void updatePart(Part part, bool updateToLatestModel)
        {
            KRnDUpgrade upgradesToApply;
            if (updateToLatestModel)
            {
                if (KRnD.upgrades.TryGetValue(KRnD.sanatizePartName(part.name), out upgradesToApply))
                {
                    // Apply upgrades from global list:
                    KRnD.updatePart(part, upgradesToApply);
                }
                else
                {
                    // No Upgrades found, applay base-stats:
                    upgradesToApply = new KRnDUpgrade();
                    KRnD.updatePart(part, upgradesToApply);
                }
            }
            else
            {
                // Extract current upgrades of the part and set thoes stats:
                KRnDModule rndModule = KRnD.getKRnDModule(part);
                if (rndModule != null && (upgradesToApply = rndModule.getCurrentUpgrades()) != null)
                {
                    // Apply upgrades from the RnD-Module:
                    KRnD.updatePart(part, upgradesToApply);
                }
                else
                {
                    // No Upgrades found, applay base-stats:
                    upgradesToApply = new KRnDUpgrade();
                    KRnD.updatePart(part, upgradesToApply);
                }
            }
        }

        // Sometimes the name of the root-part of a vessel is extended by the vessel-name like "Mk1Pod (X-Bird)", this function can be used
        // as wrapper to always return the real name:
        public static string sanatizePartName(string partName)
        {
            return Regex.Replace(partName, @" \(.*\)$", "");
        }

        // Updates the given part with all upgrades provided in "upgradesToApply".
        public static void updatePart(Part part, KRnDUpgrade upgradesToApply)
        {
            try
            {
                // Find all relevant modules of this part:
                KRnDModule rndModule = KRnD.getKRnDModule(part);
                if (rndModule == null) return;
                if (KRnD.upgrades == null) throw new Exception("upgrades-dictionary missing");
                if (KRnD.originalStats == null) throw new Exception("original-stats-dictionary missing");

                // Get the part-name ("):
                String partName = KRnD.sanatizePartName(part.name);

                // Get the original part-stats:
                PartStats originalStats;
                if (!KRnD.originalStats.TryGetValue(partName, out originalStats)) throw new Exception("no original-stats for part '" + partName + "'");

                KRnDUpgrade latestModel;
                if (!KRnD.upgrades.TryGetValue(partName, out latestModel)) latestModel = null;


                // Dry Mass:
                rndModule.dryMass_upgrades = upgradesToApply.dryMass;
                float dryMassFactor = 1 + KRnD.calculateImprovementFactor(rndModule.dryMass_improvement, rndModule.dryMass_improvementScale, upgradesToApply.dryMass);
                part.mass = originalStats.mass * dryMassFactor;
                part.prefabMass = part.mass; // New in ksp 1.1, if this is correct is just guesswork however...

                if (originalStats.hasVariants)
                {
                    part.partInfo.variant.Mass = originalStats.currentVariantMass * dryMassFactor;
                    UpdatePartVariantMasses(part, originalStats, dryMassFactor);
                    part.baseVariant.Mass = originalStats.variantBaseMass * dryMassFactor;
                }

                // Dry Mass also improves fairing mass:
                ModuleProceduralFairing fairngModule = KRnD.getFairingModule(part);
                if (fairngModule)
                {
                    fairngModule.UnitAreaMass = originalStats.fairingAreaMass * dryMassFactor;
                }

                // Max Int/Skin Temp:
                rndModule.maxTemperature_upgrades = upgradesToApply.maxTemperature;
                double tempFactor = (1 + KRnD.calculateImprovementFactor(rndModule.maxTemperature_improvement, rndModule.maxTemperature_improvementScale, upgradesToApply.maxTemperature));
                part.skinMaxTemp = originalStats.skinMaxTemp * tempFactor;
                part.maxTemp = originalStats.intMaxTemp * tempFactor;

                // Fuel Flow:
                List<ModuleEngines> engineModules = KRnD.getEngineModules(part);
                ModuleRCS rcsModule = KRnD.getRcsModule(part);
                if (engineModules != null || rcsModule)
                {
                    rndModule.fuelFlow_upgrades = upgradesToApply.fuelFlow;
                    for (int i = 0; i < originalStats.maxFuelFlows.Count; i++)
                    {
                        float maxFuelFlow = originalStats.maxFuelFlows[i] * (1 + KRnD.calculateImprovementFactor(rndModule.fuelFlow_improvement, rndModule.fuelFlow_improvementScale, upgradesToApply.fuelFlow));
                        if (engineModules != null)
                            engineModules[i].maxFuelFlow = maxFuelFlow;
                        else if (rcsModule)
                            rcsModule.thrusterPower = maxFuelFlow; // There is only one rcs-module
                    }
                }
                else
                {
                    rndModule.fuelFlow_upgrades = 0;
                }

                // ISP Vac & Atm:
                if (engineModules != null || rcsModule)
                {
                    rndModule.ispVac_upgrades = upgradesToApply.ispVac;
                    rndModule.ispAtm_upgrades = upgradesToApply.ispAtm;
                    float improvementFactorVac = 1 + KRnD.calculateImprovementFactor(rndModule.ispVac_improvement, rndModule.ispVac_improvementScale, upgradesToApply.ispVac);
                    float improvementFactorAtm = 1 + KRnD.calculateImprovementFactor(rndModule.ispAtm_improvement, rndModule.ispAtm_improvementScale, upgradesToApply.ispAtm);

                    for (int i = 0; i < originalStats.atmosphereCurves.Count; i++)
                    {
                        bool isAirbreather = false;
                        if (engineModules != null) isAirbreather = engineModules[i].engineType == EngineType.Turbine || engineModules[i].engineType == EngineType.Piston || engineModules[i].engineType == EngineType.ScramJet;
                        FloatCurve fc = new FloatCurve();
                        for (int v = 0; v < originalStats.atmosphereCurves[i].Curve.length; v++)
                        {
                            Keyframe frame = originalStats.atmosphereCurves[i].Curve[v];

                            float pressure = frame.time;
                            float factorAtThisPressure = 1;
                            if (isAirbreather && originalStats.atmosphereCurves[i].Curve.length == 1) factorAtThisPressure = improvementFactorAtm; // Airbreathing engines have a preassure curve starting at 0, but they should use Atm. as improvement factor.
                            else if (pressure == 0) factorAtThisPressure = improvementFactorVac; // In complete vacuum
                            else if (pressure >= 1) factorAtThisPressure = improvementFactorAtm; // At lowest kerbal atmosphere
                            else
                            {
                                factorAtThisPressure = (1 - pressure) * improvementFactorVac + pressure * improvementFactorAtm; // Mix both
                            }
                            float newValue = frame.value * factorAtThisPressure;
                            fc.Add(pressure, newValue);
                        }
                        if (engineModules != null) engineModules[i].atmosphereCurve = fc;
                        else if (rcsModule) rcsModule.atmosphereCurve = fc; // There is only one rcs-module
                    }
                }
                else
                {
                    rndModule.ispVac_upgrades = 0;
                    rndModule.ispAtm_upgrades = 0;
                }

                // Torque:
                ModuleReactionWheel reactionWheel = KRnD.getReactionWheelModule(part);
                if (reactionWheel)
                {
                    rndModule.torque_upgrades = upgradesToApply.torque;
                    float torque = originalStats.torque * (1 + KRnD.calculateImprovementFactor(rndModule.torque_improvement, rndModule.torque_improvementScale, upgradesToApply.torque));
                    reactionWheel.PitchTorque = torque;
                    reactionWheel.YawTorque = torque;
                    reactionWheel.RollTorque = torque;
                }
                else
                {
                    rndModule.torque_upgrades = 0;
                }

                // Antenna Range: WIP
                //ModuleDataTransmitter dataTransmitter = KRnD.getDataTransmitterModule(part);
                //if (dataTransmitter)
                //{
                //    rndModule.antPower_upgrades = upgradesToApply.antPower;
                //    double antPower = originalStats.antPower * (1 + KRnD.calculateImprovementFactor(rndModule.antPower_improvement, rndModule.antPower_improvementScale, upgradesToApply.antPower));
                //    dataTransmitter.antennaPower = antPower;
                //}
                //else
                //{
                //    rndModule.antPower_upgrades = 0;
                //}

                // Drill Efficiency:
                ModuleResourceHarvester resourceHarvester = KRnD.getResourceHarvesterModule(part);
                if (resourceHarvester)
                {
                    rndModule.harvester_upgrades = upgradesToApply.harvester;
                    float harvester = originalStats.harvester * (1 + KRnD.calculateImprovementFactor(rndModule.harvester_improvement, rndModule.harvester_improvementScale, upgradesToApply.harvester));
                    resourceHarvester.Efficiency = harvester;
                }
                else
                {
                    rndModule.harvester_upgrades = 0;
                }

                // Radiator Efficiency:
                ModuleActiveRadiator activeRadiator = KRnD.getActiveRadiatorModule(part);
                if (activeRadiator)
                {
                    rndModule.radiatorEfficiency_upgrades = upgradesToApply.radiatorEfficiency;
                    double radiatorEfficiency = originalStats.radiatorEfficiency * (1 + KRnD.calculateImprovementFactor(rndModule.radiatorEfficiency_improvement, rndModule.radiatorEfficiency_improvementScale, upgradesToApply.radiatorEfficiency));
                    radiatorEfficiency = Math.Round(radiatorEfficiency);// Don't want decimals
                    activeRadiator.maxEnergyTransfer = radiatorEfficiency;
                }
                else
                {
                    rndModule.radiatorEfficiency_upgrades = 0;
                }

                // Charge Rate:
                ModuleDeployableSolarPanel solarPanel = KRnD.getSolarPanelModule(part);
                if (solarPanel)
                {
                    rndModule.chargeRate_upgrades = upgradesToApply.chargeRate;
                    float chargeEfficiency = (1 + KRnD.calculateImprovementFactor(rndModule.chargeRate_improvement, rndModule.chargeRate_improvementScale, upgradesToApply.chargeRate));
                    // Somehow changing the charge-rate stopped working in KSP 1.1, so we use the efficiency instead. This however does not
                    // show up in the module-info (probably a bug in KSP), which is why we have another workaround to update the info-texts.
                    // float chargeRate = originalStats.chargeRate * chargeEfficiency;
                    // solarPanel.chargeRate = chargeRate;
                    solarPanel.efficiencyMult = chargeEfficiency;
                }
                else
                {
                    rndModule.chargeRate_upgrades = 0;
                }

                // Crash Tolerance (only for landing legs):
                ModuleWheelBase landingLeg = KRnD.getLandingLegModule(part);
                if (landingLeg)
                {
                    rndModule.crashTolerance_upgrades = upgradesToApply.crashTolerance;
                    float crashTolerance = originalStats.crashTolerance * (1 + KRnD.calculateImprovementFactor(rndModule.crashTolerance_improvement, rndModule.crashTolerance_improvementScale, upgradesToApply.crashTolerance));
                    part.crashTolerance = crashTolerance;
                }
                else
                {
                    rndModule.crashTolerance_upgrades = 0;
                }

                // Battery Charge:
                PartResource electricCharge = KRnD.getChargeResource(part);
                if (electricCharge != null)
                {
                    rndModule.batteryCharge_upgrades = upgradesToApply.batteryCharge;
                    double batteryCharge = originalStats.batteryCharge * (1 + KRnD.calculateImprovementFactor(rndModule.batteryCharge_improvement, rndModule.batteryCharge_improvementScale, upgradesToApply.batteryCharge));
                    batteryCharge = Math.Round(batteryCharge); // We don't want half units of electric charge

                    bool batteryIsFull = false;
                    if (electricCharge.amount == electricCharge.maxAmount) batteryIsFull = true;

                    electricCharge.maxAmount = batteryCharge;
                    if (batteryIsFull) electricCharge.amount = electricCharge.maxAmount;
                }
                else
                {
                    rndModule.batteryCharge_upgrades = 0;
                }

                // Generator & Fission-Generator Efficiency:
                ModuleGenerator generator = KRnD.getGeneratorModule(part);
                PartModule fissionGenerator = KRnD.getFissionGeneratorModule(part);
                if (generator || fissionGenerator)
                {
                    rndModule.generatorEfficiency_upgrades = upgradesToApply.generatorEfficiency;

                    if (generator)
                    {
                        for (int i = 0; i < generator.resHandler.outputResources.Count; i++)
                        {
                            ModuleResource outputResource = generator.resHandler.outputResources[i];

                            double originalRate;
                            if (!originalStats.generatorEfficiency.TryGetValue(outputResource.name, out originalRate)) continue;
                            outputResource.rate = (float)(originalRate * (1 + KRnD.calculateImprovementFactor(rndModule.generatorEfficiency_improvement, rndModule.generatorEfficiency_improvementScale, upgradesToApply.generatorEfficiency)));
                        }
                    }

                    if (fissionGenerator)
                    {
                        double powerGeneration = (double)(originalStats.fissionPowerGeneration * (1 + KRnD.calculateImprovementFactor(rndModule.generatorEfficiency_improvement, rndModule.generatorEfficiency_improvementScale, upgradesToApply.generatorEfficiency)));
                        KRnD.setGenericModuleValue(fissionGenerator, "PowerGeneration", powerGeneration);
                    }
                }
                else
                {
                    rndModule.generatorEfficiency_upgrades = 0;
                }

                // Converter Efficiency:
                List<ModuleResourceConverter> converterList = KRnD.getConverterModules(part);
                if (converterList != null)
                {
                    for (int i = 0; i < converterList.Count; i++)
                    {
                        ModuleResourceConverter converter = converterList[i];

                        Dictionary<String, double> origiginalOutputResources;
                        if (!originalStats.converterEfficiency.TryGetValue(converter.ConverterName, out origiginalOutputResources)) continue;

                        rndModule.converterEfficiency_upgrades = upgradesToApply.converterEfficiency;
                        // Since KSP 1.2 this can't be done in a foreach anymore, we have to read and write back the entire ResourceRatio-Object:
                        for (int i1 = 0; i1 < converter.outputList.Count; i1++)
                        {
                            ResourceRatio resourceRatio = converter.outputList[i1];
                            double originalRatio;
                            if (!origiginalOutputResources.TryGetValue(resourceRatio.ResourceName, out originalRatio)) continue;
                            resourceRatio.Ratio = (float)(originalRatio * (1 + KRnD.calculateImprovementFactor(rndModule.converterEfficiency_improvement, rndModule.converterEfficiency_improvementScale, upgradesToApply.converterEfficiency)));
                            converter.outputList[i1] = resourceRatio;
                        }
                    }
                }
                else
                {
                    rndModule.converterEfficiency_upgrades = 0;
                }

                // Parachute Strength:
                ModuleParachute parachute = KRnD.getParachuteModule(part);
                if (parachute)
                {
                    rndModule.parachuteStrength_upgrades = upgradesToApply.parachuteStrength;
                    double chuteMaxTemp = originalStats.chuteMaxTemp * (1 + KRnD.calculateImprovementFactor(rndModule.parachuteStrength_improvement, rndModule.parachuteStrength_improvementScale, upgradesToApply.parachuteStrength));
                    parachute.chuteMaxTemp = chuteMaxTemp; // The safe deployment-speed is derived from the temperature
                }
                else
                {
                    rndModule.parachuteStrength_upgrades = 0;
                }

                // Fuel Capacity:
                List<PartResource> fuelResources = KRnD.getFuelResources(part);
                if (fuelResources != null && originalStats.fuelCapacities != null)
                {
                    rndModule.fuelCapacity_upgrades = upgradesToApply.fuelCapacity;
                    double improvementFactor = (1 + KRnD.calculateImprovementFactor(rndModule.fuelCapacity_improvement, rndModule.fuelCapacity_improvementScale, upgradesToApply.fuelCapacity));

                    for (int i = 0; i < fuelResources.Count; i++)
                    {
                        PartResource fuelResource = fuelResources[i];

                        if (!originalStats.fuelCapacities.ContainsKey(fuelResource.resourceName)) continue;
                        double originalCapacity = originalStats.fuelCapacities[fuelResource.resourceName];
                        double newCapacity = originalCapacity * improvementFactor;
                        newCapacity = Math.Round(newCapacity); // We don't want half units of fuel

                        bool tankIsFull = false;
                        if (fuelResource.amount == fuelResource.maxAmount) tankIsFull = true;

                        fuelResource.maxAmount = newCapacity;
                        if (tankIsFull) fuelResource.amount = fuelResource.maxAmount;
                    }
                }
                else
                {
                    rndModule.fuelCapacity_upgrades = 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] updatePart(" + part.name.ToString() + "): " + e.ToString());
            }
        }

        // Updates all parts of the given vessel according to their RnD-Moudle settings (should be executed
        // when the vessel is loaded to make sure, that the vessel uses its own, historic upgrades and not
        // the global part-upgrades).
        public static void updateVessel(Vessel vessel)
        {
            try
            {
                if (!vessel.isActiveVessel) return; // Only the currently active vessel matters, the others are not simulated anyway.
                if (KRnD.upgrades == null) throw new Exception("upgrades-dictionary missing");

                Log.Error("updating vessel '" + vessel.vesselName.ToString() + "'");

                // Iterate through all parts:
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part part = vessel.parts[i];

                    // We only have to update parts which have the RnD-Module:
                    KRnDModule rndModule = KRnD.getKRnDModule(part);
                    if (rndModule == null) continue;

                    if (vessel.situation == Vessel.Situations.PRELAUNCH)
                    {
                        // Update the part with the latest model while on the launchpad:
                        KRnD.updatePart(part, true);
                    }
                    else if (rndModule.upgradeToLatest > 0)
                    {
                        // Flagged by another mod (eg KSTS) to get updated to the latest model (once):
                       Log.Error("part '" + KRnD.sanatizePartName(part.name) + "' of '" + vessel.vesselName + "' was flagged to be updated to the latest model");
                        rndModule.upgradeToLatest = 0;
                        KRnD.updatePart(part, true);
                    }
                    else
                    {
                        // Update this part with its own stats:
                        KRnD.updatePart(part, false);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] updateVesselActive(): " + e.ToString());
            }
        }

        // Is called every time the active vessel changes (on entering a scene, switching the vessel or on docking).
        private void OnVesselChange(Vessel vessel)
        {
            try
            {
                KRnD.updateVessel(vessel);
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] OnVesselChange(): " + e.ToString());
            }
        }

        // Is called when we interact with a part in the editor.
        private void EditorPartEvent(ConstructionEventType ev, Part part)
        {
            try
            {
                if (ev != ConstructionEventType.PartCreated && ev != ConstructionEventType.PartDetached && ev != ConstructionEventType.PartAttached && ev != ConstructionEventType.PartDragging) return;
                KRnDGUI.selectedPart = part;
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] EditorPartEvent(): " + e.ToString());
            }
        }

        private void OnVariantApplied(Part p, PartVariant pv)
        {
            if (p == null || p != KRnDGUI.selectedPart) return;
            Log.Info("KRnD.OnVariantApplied, part: " + p.partInfo.title + ", " + p.name);

            foreach (var v in p.partInfo.Variants)
            {
                if (v.Name == pv.Name)
                {
                    //partStats.currentVariant = p.partInfo.variant.Name;
                    //partStats.currentVariantMass = kv.mass;

                    //v.Mass
                    break;
                }

            }
        }
        public List<string> getBlacklistedModules()
        {
            List<string> blacklistedModules = new List<string>();
            try
            {
                ConfigNode node = ConfigNode.Load(BLACKLIST_FILE);

                foreach (string blacklistedModule in node.GetValues("BLACKLISTED_MODULE"))
                {
                    if (!blacklistedModules.Contains(blacklistedModule)) blacklistedModules.Add(blacklistedModule);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] getBlacklistedModules(): " + e.ToString());
            }
            return blacklistedModules;
        }

        public List<string> getBlacklistedParts()
        {
            List<string> blacklistedParts = new List<string>();
            try
            {
                ConfigNode node = ConfigNode.Load(BLACKLIST_FILE);

                foreach (string blacklistedPart in node.GetValues("BLACKLISTED_PART"))
                {
                    if (!blacklistedParts.Contains(blacklistedPart)) blacklistedParts.Add(blacklistedPart);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] getBlacklistedParts(): " + e.ToString());
            }
            return blacklistedParts;
        }

        // Is called when this Addon is first loaded to initializes all values (eg registration of event-handlers and creation
        // of original-stats library).
        public void Awake()
        {
            try
            {
                // Create a list of all valid fuel resources:
                if (KRnD.fuelResources == null)
                {
                    KRnD.fuelResources = new List<string>();
                    KRnD.fuelResources.Add("MonoPropellant"); // Always use MonoPropellant as fuel (RCS-Thrusters don't have engine modules and are not found with the code below)

                    for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
                    {
                        AvailablePart aPart = PartLoader.LoadedPartsList[i];

                        Part part = aPart.partPrefab;
                        List<ModuleEngines> engineModules = KRnD.getEngineModules(part);
                        if (engineModules == null) continue;
                        for (int i1 = 0; i1 < engineModules.Count; i1++)
                        {
                            ModuleEngines engineModule = engineModules[i1];

                            if (engineModule.propellants == null) continue;
                            for (int i2 = 0; i2 < engineModule.propellants.Count; i2++)
                            {
                                Propellant propellant = engineModule.propellants[i2];

                                if (propellant.name == "ElectricCharge") continue; // Electric Charge is improved by batteries.
                                if (propellant.name == "IntakeAir") continue; // This is no real fuel-type.
                                if (!fuelResources.Contains(propellant.name)) fuelResources.Add(propellant.name);
                            }
                        }
                    }

                    String listString = "";
                    for (int i = 0; i < KRnD.fuelResources.Count; i++)
                    {
                        String fuelName = KRnD.fuelResources[i];

                        if (listString != "") listString += ", ";
                        listString += fuelName;
                    }
                   Log.Error("found " + KRnD.fuelResources.Count.ToString() + " propellants: " + listString);
                }

                // Create a list of blacklisted parts (parts with known incompatible modules of other mods):
                if (KRnD.blacklistedParts == null)
                {
                    KRnD.blacklistedParts = getBlacklistedParts();
                    List<string> blacklistedModules = getBlacklistedModules();

                    for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
                    {
                        AvailablePart aPart = PartLoader.LoadedPartsList[i];

                        Part part = aPart.partPrefab;
                        Boolean skip = false;
                        string blacklistedModule = "N/A";

                        for (int i1 = 0; i1 < part.Modules.Count; i1++)
                        {
                            PartModule partModule = part.Modules[i1];

                            if (blacklistedModules.Contains(partModule.moduleName))
                            {
                                blacklistedModule = partModule.moduleName;
                                skip = true;
                                break;
                            }
                        }
                        if (skip)
                        {
                           Log.Error("blacklisting part '" + part.name.ToString() + "' (has blacklisted module '" + blacklistedModule.ToString() + "')");
                            if (!KRnD.blacklistedParts.Contains(part.name)) KRnD.blacklistedParts.Add(part.name);
                            continue;
                        }
                    }

                   Log.Error("blacklisted " + KRnD.blacklistedParts.Count.ToString() + " parts, which contained one of " + blacklistedModules.Count.ToString() + " blacklisted modules");
                }

                // Create a backup of all unmodified parts before we update them. We will later use these backup-parts
                // for all calculations of upgraded stats.
                if (KRnD.originalStats == null)
                {
                    KRnD.originalStats = new Dictionary<string, PartStats>();
                    for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
                    {
                        AvailablePart aPart = PartLoader.LoadedPartsList[i];

                        Part part = aPart.partPrefab;

                        // Backup this part, if it has the RnD-Module:
                        if (KRnD.getKRnDModule(part) != null)
                        {
                            PartStats duplicate;
                            if (originalStats.TryGetValue(part.name, out duplicate))
                            {
                                Debug.LogError("[KRnD] Awake(): duplicate part-name: " + part.name.ToString());
                            }
                            else
                            {
                                originalStats.Add(part.name, new PartStats(part));
                            }
                        }
                    }
                }

                // Execute the following code only once:
                if (KRnD.initialized) return;
                DontDestroyOnLoad(this);

                // Register event-handlers:
                GameEvents.onVesselChange.Add(this.OnVesselChange);
                GameEvents.onEditorPartEvent.Add(this.EditorPartEvent);
                GameEvents.onVariantApplied.Add(this.OnVariantApplied);

                KRnD.initialized = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] Awake(): " + e.ToString());
            }
        }
    }

    // This class handels load- and save-operations.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.EDITOR, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    class KRnDScenarioModule : ScenarioModule
    {
        public override void OnSave(ConfigNode node)
        {
            try
            {
                double time = DateTime.Now.Ticks;
                ConfigNode upgradeNodes = new ConfigNode("upgrades");
                foreach (string upgradeName in KRnD.upgrades.Keys)
                {
                    KRnDUpgrade upgrade;
                    if (!KRnD.upgrades.TryGetValue(upgradeName, out upgrade)) continue;
                    upgradeNodes.AddNode(upgrade.createConfigNode(upgradeName));
                   Log.Error("saved: " + upgradeName + " " + upgrade.ToString());
                }
                node.AddNode(upgradeNodes);

                time = (DateTime.Now.Ticks - time) / TimeSpan.TicksPerSecond;
               Log.Error("saved " + upgradeNodes.CountNodes.ToString() + " upgrades in " + time.ToString("0.000s"));

                ConfigNode guiSettings = new ConfigNode("gui");
                guiSettings.AddValue("left", KRnDGUI.windowPosition.xMin);
                guiSettings.AddValue("top", KRnDGUI.windowPosition.yMin);
                node.AddNode(guiSettings);
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] OnSave(): " + e.ToString());
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            try
            {
                double time = DateTime.Now.Ticks;
                int upgradesApplied = 0;

                KRnD.upgrades.Clear();

                ConfigNode upgradeNodes = node.GetNode("upgrades");
                if (upgradeNodes != null)
                {
                    foreach (ConfigNode upgradeNode in upgradeNodes.GetNodes())
                    {
                        KRnDUpgrade upgrade = KRnDUpgrade.createFromConfigNode(upgradeNode);
                        KRnD.upgrades.Add(upgradeNode.name, upgrade);
                    }

                    // Update global part-list with new upgrades from the savegame:
                    upgradesApplied = KRnD.updateGlobalParts();

                    // If we started with an active vessel, update that vessel:
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    if (vessel)
                    {
                        KRnD.updateVessel(vessel);
                    }

                    time = (DateTime.Now.Ticks - time) / TimeSpan.TicksPerSecond;
                   Log.Error("retrieved and applied " + upgradesApplied.ToString() + " upgrades in " + time.ToString("0.000s"));
                }

                ConfigNode guiSettings = node.GetNode("gui");
                if (guiSettings != null)
                {
                    if (guiSettings.HasValue("left")) KRnDGUI.windowPosition.xMin = (float)Double.Parse(guiSettings.GetValue("left"));
                    if (guiSettings.HasValue("top")) KRnDGUI.windowPosition.yMin = (float)Double.Parse(guiSettings.GetValue("top"));
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[KRnD] OnLoad(): " + e.ToString());
            }
        }
    }

}
