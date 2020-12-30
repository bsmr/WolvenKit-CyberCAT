﻿using CyberCAT.Core.Classes.Interfaces;
using CyberCAT.Core.Classes.NodeRepresentations;
using CyberCAT.Core.Enums;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CyberCAT.Core.Classes.Parsers
{
    class StatsSystemParser : GenericUnknownStructParser, INodeParser
    {
        public string ParsableNodeName { get; private set; }
        public string DisplayName { get; private set; }
        public Guid Guid { get; private set; }
        public StatsSystemParser()
        {
            ParsableNodeName = Constants.NodeNames.STATS_SYSTEM;
            DisplayName = "Stat System Parser";
            Guid = Guid.Parse("{8F3788DA-3ECD-47FC-A6F9-A50314F61AA7}");
        }
        private string ReadStringAtOffset(BinaryReader reader, long baseAddress, uint offset, int length)
        {
            var origPos = reader.BaseStream.Position;
            reader.BaseStream.Position = baseAddress + offset;
            var bytes = reader.ReadBytes(length);
            reader.BaseStream.Position = origPos;

            return Encoding.ASCII.GetString(bytes);
        }

        public object Read(NodeEntry node, BinaryReader reader, List<INodeParser> parsers)
        {
            var result = new GenericUnknownStruct();

            reader.Skip(4); //skip Id

            var nodeStartPos = reader.BaseStream.Position;

            int readSize = 0;
            if (DEBUG_WRITING)
            {
                readSize = node.Size - ((int)reader.BaseStream.Position - node.Offset);
                var tmpBuffer = reader.ReadBytes(readSize);
                File.WriteAllBytes($"C:\\Dev\\T1\\{node.Name}.bin", tmpBuffer);
                reader.BaseStream.Position = nodeStartPos;
            }

            result.TotalLength = reader.ReadUInt32();
            result.Unknown1 = reader.ReadBytes(4);
            result.Unknown2 = reader.ReadUInt32();
            result.Unknown3 = reader.ReadBytes(4);

            var stringTableOffset = reader.ReadUInt32();
            var indexTableOffset = reader.ReadUInt32();
            var dataTableOffset = reader.ReadUInt32();

            if (result.Unknown2 > 1)
            {
                // only for ScriptableSystemsContainer
                var count1 = reader.ReadInt32();

                result.CNameHashes1 = new ulong[count1];
                for (int i = 0; i < count1; i++)
                {
                    result.CNameHashes1[i] = reader.ReadUInt64();
                }
            }

            var stringTablePosition = reader.BaseStream.Position + stringTableOffset;
            var indexTablePosition = reader.BaseStream.Position + indexTableOffset;
            var dataTablePosition = reader.BaseStream.Position + dataTableOffset;

            // start of stringIndexTable
            var stringList = new List<string>();
            var stringBasePosition = reader.BaseStream.Position;
            var stringOffset = reader.ReadUInt16();
            var count2 = stringOffset / 4 - 1; // there could be another count somewhere...
            reader.Skip(1);
            var stringLength = reader.ReadByte(); // string is null terminated?

            // length - 1 because null terminated...
            // could probably ignore the stringIndexTable and read null terminated strings, then you also don't net the hacky jump after this
            stringList.Add(ReadStringAtOffset(reader, stringBasePosition, stringOffset, stringLength - 1));

            // there could be another count somewhere...
            for (int i = 0; i < count2; i++)
            {
                stringOffset = reader.ReadUInt16();
                reader.Skip(1);
                stringLength = reader.ReadByte();
                stringList.Add(ReadStringAtOffset(reader, stringBasePosition, stringOffset, stringLength - 1));
            }

            reader.BaseStream.Position = stringBasePosition + stringOffset + stringLength;

            Debug.Assert(reader.BaseStream.Position == indexTablePosition);

            var count3 = (dataTableOffset - indexTableOffset) / 8;

            var pointerList = new List<KeyValuePair<uint, uint>>();
            for (int i = 0; i < count3; i++)
            {
                pointerList.Add(new KeyValuePair<uint, uint>(reader.ReadUInt32(), reader.ReadUInt32()));
            }

            Debug.Assert(reader.BaseStream.Position == dataTablePosition);

            result.ClassList = new GenericUnknownStruct.ClassEntry[pointerList.Count];
            var pos = reader.BaseStream.Position;
            for (int i = 0; i < pointerList.Count; i++)
            {
                var offset = pointerList[i].Value - pointerList[0].Value;

                var classEntry = new GenericUnknownStruct.ClassEntry();
                classEntry.Name = stringList[(int)pointerList[i].Key];

                int length = 0;
                if (i == pointerList.Count - 1)
                {
                    length = (int)(result.TotalLength - pointerList[i].Value);
                }
                else
                {
                    length = (int)(pointerList[i + 1].Value - pointerList[i].Value);
                }

                reader.BaseStream.Position = pos + offset;
                classEntry.Fields = ReadFields(reader, stringList);

                result.ClassList[i] = classEntry;
            }

            Debug.Assert((reader.BaseStream.Position - nodeStartPos - 4) == result.TotalLength);

            // only for PSData, what the heck is this...
            readSize = node.Size - ((int)reader.BaseStream.Position - node.Offset);
            if (readSize > 0)
            {
                var count1 = reader.ReadInt32();

                result.CNameHashes2 = new ulong[count1];
                for (int i = 0; i < count1; i++)
                {
                    result.CNameHashes2[i] = reader.ReadUInt64();
                }
            }

            readSize = node.Size - ((int)reader.BaseStream.Position - node.Offset);
            Debug.Assert(readSize == 0);
            var mapping = new GameStatsStateMapStructure(result);
            StatModifierDataConverter.Convert(result, mapping);
            return result;
        }

        public GenericUnknownStruct.BaseGenericField[] ReadFields(BinaryReader reader, List<string> stringList)
        {
            var pos = reader.BaseStream.Position;
            var fieldCount = reader.ReadUInt16();

            var fieldArray = new GenericUnknownStruct.BaseGenericField[fieldCount];
            var offsetList = new uint[fieldCount];

            for (int i = 0; i < fieldCount; i++)
            {
                var field = new GenericUnknownStruct.BaseGenericField();

                var s = reader.ReadUInt16();
                if (s >= stringList.Count)
                {
                    throw new Exception();
                }
                field.Name = stringList[s];
                s = reader.ReadUInt16();
                if (s >= stringList.Count)
                {
                    throw new Exception();
                }
                field.Type = stringList[s];
                var offset = reader.ReadUInt32();

                fieldArray[i] = field;
                offsetList[i] = offset;
            }

            for (int i = 0; i < fieldArray.Length; i++)
            {
                reader.BaseStream.Position = pos + offsetList[i];
                // TODO: ...
                var fieldType = fieldArray[i].Type.Replace("[2]", "array:");
                var typeParts = fieldType.Split(':');

                var val = ReadValue(reader, stringList, typeParts, 0);

                if (val != null)
                {
                    var type = typeof(GenericUnknownStruct.GenericField<>).MakeGenericType(val.GetType());
                    dynamic field = Activator.CreateInstance(type, val);
                    field.Name = fieldArray[i].Name;
                    field.Type = fieldArray[i].Type;

                    fieldArray[i] = field;
                }
            }

            return fieldArray;
        }

        public object ReadValue(BinaryReader reader, List<string> stringList, string[] typeParts, int index)
        {
            var dataType = typeParts[index];

            switch (dataType)
            {
                case "array":
                    var arraySize = reader.ReadUInt32();
                    object result = null;
                    for (int i = 0; i < arraySize; i++)
                    {
                        var val = ReadValue(reader, stringList, typeParts, index + 1);
                        if (i == 0)
                        {
                            result = Array.CreateInstance(val.GetType(), arraySize);
                        }
                        ((Array)result).SetValue(val, i);
                    }
                    return result;

                case "Int32":
                    return reader.ReadInt32();

                case "Uint32":
                case "handle":
                    return reader.ReadUInt32();

                case "Bool":
                    return reader.ReadByte() != 0;

                case "Uint64":
                case "TweakDBID":
                    return reader.ReadUInt64();

                case "Float":
                    return reader.ReadSingle();

                case "static":
                    return reader.ReadBytes(8);

                // enums
                case "ActiveMode":
                case "ActorVisibilityStatus":
                case "AdditionalTraceType":
                case "AIactionParamsPackageTypes":
                case "AIArgumentType":
                case "AIbehaviorCompletionStatus":
                case "AIbehaviorConditionOutcomes":
                case "AIbehaviorUpdateOutcome":
                case "AICombatSectorType":
                case "AICombatSpaceSize":
                case "AICommandState":
                case "AICoverExposureMethod":
                case "AIEExecutionOutcome":
                case "AIEInterruptionOutcome":
                case "aimTypeEnum":
                case "AIParameterizationType":
                case "AIReactionCountOutcome":
                case "AISignalFlags":
                case "AISquadType":
                case "AIThreatPersistenceStatus":
                case "AITrackedStatusType":
                case "AIUninterruptibleActionType":
                case "animAimState":
                case "animCoverAction":
                case "animCoverState":
                case "animHitReactionType":
                case "animLookAtChestMode":
                case "animLookAtEyesMode":
                case "animLookAtHeadMode":
                case "animLookAtLeftHandedMode":
                case "animLookAtLimitDegreesType":
                case "animLookAtLimitDistanceType":
                case "animLookAtRightHandedMode":
                case "animLookAtStatus":
                case "animLookAtStyle":
                case "animLookAtTwoHandedMode":
                case "animNPCVehicleDeathType":
                case "animStanceState":
                case "animWeaponOwnerType":
                case "AttitudeChange":
                case "AttributeButtonState":
                case "audioAudioEventFlags":
                case "audioEventActionType":
                case "BlacklistReason":
                case "braindanceVisionMode":
                case "ButtonStatus":
                case "CharacterScreenType":
                case "ClueState":
                case "CodexCategoryType":
                case "CodexDataSource":
                case "CodexImageType":
                case "coverLeanDirection":
                case "CrafringMaterialItemHighlight":
                case "CraftingCommands":
                case "CraftingInfoType":
                case "CraftingMode":
                case "CraftingNotificationType":
                case "CustomButtonType":
                case "CustomWeaponWheelSlot":
                case "CyberwareInfoType":
                case "CyberwareScreenType":
                case "DamageEffectDisplayType":
                case "damageSystemLogFlags":
                case "DerivedFilterResult":
                case "DeviceStimType":
                case "DMGPipelineType":
                case "DronePose":
                case "DropdownDisplayContext":
                case "DropdownItemDirection":
                case "DropPointPackageStatus":
                case "EActionContext":
                case "EActionInactivityReson":
                case "EActionsSequencerMode":
                case "EActionType":
                case "EActivationState":
                case "EAIActionPhase":
                case "EAIActionState":
                case "EAIActionTarget":
                case "EAIAttitude":
                case "EAIBackgroundCombatStep":
                case "EAICombatPreset":
                case "EAICoverAction":
                case "EAICoverActionDirection":
                case "EAIDismembermentBodyPart":
                case "EAIGateEventFlags":
                case "EAIGateSignalFlags":
                case "EAIHitBodyPart":
                case "EAIHitDirection":
                case "EAIHitIntensity":
                case "EAIHitSource":
                case "EAILastHitReactionPlayed":
                case "EAimAssistLevel":
                case "EAIPlayerSquadOrder":
                case "EAIRole":
                case "EAIShootingPatternRange":
                case "EAISquadAction":
                case "EAISquadChoiceAlgorithm":
                case "EAISquadRing":
                case "EAISquadTactic":
                case "EAISquadVerb":
                case "EAITargetType":
                case "EAIThreatCalculationType":
                case "EAITicketStatus":
                case "EAllowedTo":
                case "EAnimationType":
                case "EArgumentType":
                case "EAttackType":
                case "EAxisType":
                case "EBarkList":
                case "EBeamStyle":
                case "EBinkOperationType":
                case "EBOOL":
                case "EBreachOrigin":
                case "EBroadcasteingType":
                case "ECallbackExpressionActions":
                case "ECameraDirectionFunctionalTestsUtil":
                case "ECarryState":
                case "ECartOperationResult":
                case "ECentaurShieldState":
                case "ECLSForcedState":
                case "ECompanionDistancePreset":
                case "ECompanionPositionPreset":
                case "ECompareOp":
                case "EComparisonOperator":
                case "EComparisonType":
                case "EComponentOperation":
                case "EComputerAnimationState":
                case "EComputerMenuType":
                case "EConclusionQuestState":
                case "ECooldownGameControllerMode":
                case "ECooldownIndicatorState":
                case "ECoverSpecialAction":
                case "ECraftingIconPositioning":
                case "EDeathType":
                case "EDebuggerColor":
                case "EDeviceChallengeAttribute":
                case "EDeviceChallengeSkill":
                case "EDeviceDurabilityState":
                case "EDeviceDurabilityType":
                case "EDeviceStatus":
                case "EDocumentType":
                case "EDodgeMovementInput":
                case "EDoorOpeningType":
                case "EDoorSkillcheckSide":
                case "EDoorStatus":
                case "EDoorTriggerSide":
                case "EDoorType":
                case "EDownedType":
                case "EDPadSlot":
                case "EDrillMachineRewireState":
                case "EEffectOperationType":
                case "EEquipmentSetType":
                case "EEquipmentSide":
                case "EEquipmentState":
                case "EExplosiveAdditionalGameEffectType":
                case "EFastTravelSystemInstruction":
                case "EFastTravelTriggerType":
                case "EFilterType":
                case "EFocusClueInvestigationState":
                case "EFocusForcedHighlightType":
                case "EFocusOutlineType":
                case "EForcedElevatorArrowsState":
                case "EGameplayChallengeLevel":
                case "EGameplayRole":
                case "EGameSessionDataType":
                case "EGenericNotificationPriority":
                case "EGlitchState":
                case "EGravityType":
                case "EGrenadeType":
                case "EHandEquipSlot":
                case "EHitReactionMode":
                case "EHitReactionZone":
                case "EHitShapeType":
                case "EHotkey":
                case "EHotkeyRequestType":
                case "EHudAvatarMode":
                case "EHudPhoneFunction":
                case "EHudPhoneVisibility":
                case "EIndustrialArmAnimations":
                case "EInitReactionAnim":
                case "EInkAnimationPlaybackOption":
                case "EInputCustomKey":
                case "EInputKey":
                case "EInventoryComboBoxMode":
                case "EInventoryDataStatDisplayType":
                case "EItemOperationType":
                case "EItemSlotCheckType":
                case "EJuryrigTrapState":
                case "EKnockdownStates":
                case "ELastUsed":
                case "ELauncherActionType":
                case "ELaunchMode":
                case "ELayoutType":
                case "ELightSequenceStage":
                case "ELightSwitchRandomizerType":
                case "ELinkType":
                case "ELogicOperator":
                case "ELogType":
                case "EMagazineAmmoState":
                case "EMappinDisplayMode":
                case "EMappinVisualState":
                case "EMathOperationType":
                case "EMathOperator":
                case "EMeasurementSystem":
                case "EMeasurementUnit":
                case "EMeleeAttacks":
                case "EMeleeAttackType":
                case "EMissileRainPhase":
                case "EMoveAssistLevel":
                case "EMovementDirection":
                case "ENetworkRelation":
                case "ENeutralizeType":
                case "ENPCPhaseState":
                case "ENPCTelemetryData":
                case "entAttachmentTarget":
                case "entAudioDismembermentPart":
                case "EntityNotificationType":
                case "entragdollActivationRequestType":
                case "EOperationClassType":
                case "EOutlineType":
                case "EPaymentSchedule":
                case "EPermissionSource":
                case "EPersonalLinkConnectionStatus":
                case "EPersonalLinkSlotSide":
                case "EPingType":
                case "EPlayerMovementDirection":
                case "EPlaystyle":
                case "EPlaystyleType":
                case "EPowerDifferential":
                case "EPreventionDebugProcessReason":
                case "EPreventionHeatStage":
                case "EPreventionPsychoLogicType":
                case "EPreventionSystemInstruction":
                case "EPriority":
                case "EProgressBarContext":
                case "EProgressBarType":
                case "EQuestFilterType":
                case "EQuestVehicleDoorState":
                case "EQuestVehicleWindowState":
                case "EquipmentManipulationAction":
                case "EquipmentManipulationRequestSlot":
                case "EquipmentManipulationRequestType":
                case "EquipmentPriority":
                case "ERadialMode":
                case "ERadioStationList":
                case "EReactionValue":
                case "ERenderingPlane":
                case "ERentStatus":
                case "EReprimandInstructions":
                case "ERevealDurationType":
                case "ERevealPlayerType":
                case "ERevealState":
                case "EScreenRatio":
                case "ESecurityAccessLevel":
                case "ESecurityAreaType":
                case "ESecurityGateEntranceType":
                case "ESecurityGateResponseType":
                case "ESecurityGateScannerIssueType":
                case "ESecurityGateStatus":
                case "ESecurityNotificationType":
                case "ESecuritySystemState":
                case "ESecurityTurretStatus":
                case "ESecurityTurretType":
                case "ESensorDeviceStates":
                case "ESensorDeviceWakeState":
                case "EShouldChangeAttitude":
                case "ESlotState":
                case "ESmartBulletPhase":
                case "ESmartHousePreset":
                case "ESoundStatusEffects":
                case "ESpaceFillMode":
                case "EStatProviderDataSource":
                case "EStatusEffectBehaviorType":
                case "EStatusEffects":
                case "EstatusEffectsState":
                case "ESurveillanceCameraState":
                case "ESurveillanceCameraStatus":
                case "ESwitchAction":
                case "ESystems":
                case "ETakedownActionType":
                case "ETakedownBossName":
                case "ETargetManagerAnimGraphState":
                case "ETauntType":
                case "ETelemetryData":
                case "EToggleActivationTypeComputer":
                case "EToggleOperationType":
                case "ETooltipsStyle":
                case "ETransformAnimationOperationType":
                case "ETransitionMode":
                case "ETrap":
                case "ETrapEffects":
                case "ETriggerOperationType":
                case "ETVChannel":
                case "ETweakAINodeType":
                case "EUIActionState":
                case "EUIStealthIconType":
                case "EUploadProgramState":
                case "EVarDBMode":
                case "EVehicleDoor":
                case "EVehicleWindowState":
                case "EVendorMode":
                case "EViabilityDecision":
                case "EVirtualSystem":
                case "EVisualizerActivityState":
                case "EVisualizerType":
                case "EWeaponNamesList":
                case "EWidgetPlacementType":
                case "EWidgetState":
                case "EWindowBlindersStates":
                case "EWorkspotOperationType":
                case "EWorldMapView":
                case "EWoundedBodyPart":
                case "ExplosiveTriggerDeviceLaserState":
                case "ExtraEffect":
                case "Ft_Result":
                case "Ft_TakedownStage":
                case "Ft_TakedownType":
                case "FTNpcMountingState":
                case "FTScriptState":
                case "FunctionalTestsResultCode":
                case "gameaudioeventsSurfaceDirection":
                case "gamecheatsystemFlag":
                case "gameCityAreaType":
                case "gameCombinedStatOperation":
                case "gameContactType":
                case "gameCoverHeight":
                case "gameDamageCallbackType":
                case "gameDamagePipelineStage":
                case "gamedataAchievement":
                case "gamedataAffiliation":
                case "gamedataAIActionSecurityAreaType":
                case "gamedataAIActionSecurityNotificationType":
                case "gamedataAIActionTarget":
                case "gamedataAIActionType":
                case "gamedataAIAdditionalTraceType":
                case "gamedataAIDirectorEntryStartType":
                case "gamedataAIExposureMethodType":
                case "gamedataAimAssistType":
                case "gamedataAIRingType":
                case "gamedataAIRole":
                case "gamedataAISmartCompositeType":
                case "gamedataAISquadType":
                case "gamedataAITacticType":
                case "gamedataAITicketType":
                case "gamedataArchetypeType":
                case "gamedataAttackSubtype":
                case "gamedataAttackType":
                case "gamedataBuildType":
                case "gamedataChargeStep":
                case "gamedataChoiceCaptionPartType":
                case "gamedataCompanionDistancePreset":
                case "gamedataConsumableBaseName":
                case "gamedataConsumableType":
                case "gamedataDamageType":
                case "gamedataDefenseMode":
                case "gamedataDevelopmentPointType":
                case "gamedataDistrict":
                case "gamedataEquipmentArea":
                case "gamedataEthnicity":
                case "gamedataFxAction":
                case "gamedataFxActionType":
                case "gamedataGender":
                case "gamedataGrenadeDeliveryMethodType":
                case "gamedataHitPrereqConditionType":
                case "gamedataImprovementRelation":
                case "gamedataItemCategory":
                case "gamedataItemStructure":
                case "gamedataItemType":
                case "gamedataLifePath":
                case "gamedataLocomotionMode":
                case "gamedataMappinPhase":
                case "gamedataMappinVariant":
                case "gamedataMeleeAttackDirection":
                case "gamedataMetaQuest":
                case "gamedataMovementType":
                case "gamedataNPCBehaviorState":
                case "gamedataNPCHighLevelState":
                case "gamedataNPCQuestAffiliation":
                case "gamedataNPCRarity":
                case "gamedataNPCStanceState":
                case "gamedataNPCType":
                case "gamedataNPCUpperBodyState":
                case "gamedataObjectActionReference":
                case "gamedataObjectActionType":
                case "gamedataOutput":
                case "gamedataParentAttachmentType":
                case "gamedataPerkArea":
                case "gamedataPerkType":
                case "gamedataPerkUtility":
                case "gamedataPingType":
                case "gamedataPlayerBuild":
                case "gamedataPlayerPossesion":
                case "gamedataProficiencyType":
                case "gamedataProjectileLaunchMode":
                case "gamedataProjectileOnCollisionAction":
                case "gamedataQuality":
                case "gamedataReactionPresetType":
                case "gamedataSenseObjectType":
                case "gamedataSpawnableObjectPriority":
                case "gamedataStatPoolType":
                case "gamedataStatType":
                case "gamedataStatusEffectAIBehaviorFlag":
                case "gamedataStatusEffectAIBehaviorType":
                case "gamedataStatusEffectType":
                case "gamedataStatusEffectVariation":
                case "gamedataStimPriority":
                case "gamedataStimPropagation":
                case "gamedataStimType":
                case "gamedataSubCharacter":
                case "gamedataTrackingMode":
                case "gamedataTraitType":
                case "gamedataTriggerMode":
                case "gamedataUICondition":
                case "gamedataUIIconCensorFlag":
                case "gamedataUINameplateDisplayType":
                case "gamedataVehicleManufacturer":
                case "gamedataVehicleModel":
                case "gamedataVehicleType":
                case "gamedataVendorType":
                case "gamedataWeaponEvolution":
                case "gamedataWeaponManufacturer":
                case "gamedataWorkspotActionType":
                case "gamedataWorkspotCategory":
                case "gamedataWorkspotReactionType":
                case "gamedataWorldMapFilter":
                case "gameDebugViewETextAlignment":
                case "gamedeviceActionPropertyFlags":
                case "gamedeviceRequestType":
                case "gameDifficulty":
                case "gameDismBodyPart":
                case "gameDismWoundType":
                case "gameEActionStatus":
                case "gameEContinuousMode":
                case "gameEEquipmentManagerState":
                case "gameEnemyStealthAwarenessState":
                case "gameEntitySpawnerEventType":
                case "gameEPrerequisiteType":
                case "gameEquipAnimationType":
                case "gameEStatFlags":
                case "gameeventsDeathDirection":
                case "gameGodModeType":
                case "gameGrenadeThrowStartType":
                case "gameinfluenceCollisionTestOutcome":
                case "gameinfluenceTestLineResult":
                case "gameinputActionType":
                case "gameinteractionsBumpIntensity":
                case "gameinteractionsBumpLocation":
                case "gameinteractionsBumpSide":
                case "gameinteractionsChoiceType":
                case "gameinteractionsEInteractionEventType":
                case "gameinteractionsELootChoiceType":
                case "gameinteractionsELootVisualiserControlOperation":
                case "gameinteractionsReactionState":
                case "gameItemEquipContexts":
                case "gameItemUnequipContexts":
                case "gameJournalBriefingContentType":
                case "gameJournalEntryState":
                case "gameJournalListenerType":
                case "gameJournalQuestType":
                case "gameKillType":
                case "gameLoSMode":
                case "gamemappinsMappinTargetType":
                case "gamemappinsVerticalPositioning":
                case "gameMessageSender":
                case "gameMountingObjectType":
                case "gameMountingRelationshipType":
                case "gameMountingSlotRole":
                case "gameMovingPlatformLoopType":
                case "gameMovingPlatformMovementInitializationType":
                case "gamePlatformMovementState":
                case "gamePlayerCoverDirection":
                case "gamePlayerCoverMode":
                case "gamePlayerObstacleSystemQueryType":
                case "gamePlayerStateMachine":
                case "GameplayTier":
                case "gameprojectileELaunchMode":
                case "gameprojectileOnCollisionAction":
                case "gamePSMBodyCarrying":
                case "gamePSMBodyCarryingLocomotion":
                case "gamePSMBodyCarryingStyle":
                case "gamePSMCombat":
                case "gamePSMCombatGadget":
                case "gamePSMCrosshairStates":
                case "gamePSMDetailedBodyDisposal":
                case "gamePSMDetailedLocomotionStates":
                case "gamePSMFallStates":
                case "gamePSMHighLevel":
                case "gamePSMLandingState":
                case "gamePSMLeftHandCyberware":
                case "gamePSMLocomotionStates":
                case "gamePSMMelee":
                case "gamePSMMeleeWeapon":
                case "gamePSMNanoWireLaunchMode":
                case "gamePSMRangedWeaponStates":
                case "gamePSMReaction":
                case "gamePSMStamina":
                case "gamePSMSwimming":
                case "gamePSMTakedown":
                case "gamePSMTimeDilation":
                case "gamePSMUIState":
                case "gamePSMUpperBodyStates":
                case "gamePSMVehicle":
                case "gamePSMVision":
                case "gamePSMVisionDebug":
                case "gamePSMVitals":
                case "gamePSMWeaponStates":
                case "gamePSMWhip":
                case "gamePSMWorkspotState":
                case "gamePSMZones":
                case "gameReprimandMappinAnimationState":
                case "gameSaveLockReason":
                case "gameScanningMode":
                case "gameScanningState":
                case "gameSceneAnimationMotionActionParamsPlacementMode":
                case "gameScriptedBlackboardStorage":
                case "gameSharedInventoryTag":
                case "gamesmartGunTargetState":
                case "gamestateMachineParameterAspect":
                case "gameStatModifierType":
                case "gameStatObjectsRelation":
                case "gameStatPoolModificationTypes":
                case "gameStoryTier":
                case "gametargetingSystemETargetFilterStatus":
                case "gameTelemetryDamageSituation":
                case "gameTickableEventState":
                case "gameTutorialBracketType":
                case "gameuiAuthorisationNotificationType":
                case "gameuiCharacterCustomizationPart":
                case "gameuiDamageDigitsMode":
                case "gameuiDamageDigitsStickingMode":
                case "gameuiDamageIndicatorMode":
                case "gameuiEBraindanceLayer":
                case "gameuiEClueDescriptorMode":
                case "gameuiEWorldMapDistrictView":
                case "gameuiHitType":
                case "gameuiMappinGroupState":
                case "gameVisionModeType":
                case "gameweaponReloadStatus":
                case "GenericMessageNotificationResult":
                case "GenericMessageNotificationType":
                case "GenericNotificationType":
                case "GOGRewardsSystemErrors":
                case "GOGRewardsSystemStatus":
                case "GrenadeDamageType":
                case "grsHeistStatus":
                case "HackingMinigameState":
                case "HighlightContext":
                case "HighlightMode":
                case "hitFlag":
                case "HitShape_Type":
                case "HoverStatus":
                case "HubMenuCharacterItems":
                case "HubMenuCraftingItems":
                case "HubMenuDatabaseItems":
                case "HubMenuInventoryItems":
                case "HubMenuItems":
                case "HubVendorMenuItems":
                case "HUDActorStatus":
                case "HUDActorType":
                case "HUDContext":
                case "HUDState":
                case "inkEButtonState":
                case "inkEScrollDirection":
                case "inkESliderDirection":
                case "inkEToggleState":
                case "inkIconResult":
                case "inkLoadingScreenType":
                case "inkMenuMode":
                case "inkMenuState":
                case "inkSelectorChangeDirection":
                case "inputContextType":
                case "InstanceState":
                case "IntercomStatus":
                case "InventoryItemAttachmentType":
                case "InventoryModes":
                case "InventoryPaperdollZoomArea":
                case "InventoryTooltipDisplayContext":
                case "ItemAdditionalInfoType":
                case "ItemComparisonState":
                case "ItemDisplayContext":
                case "ItemDisplayType":
                case "ItemFilterCategory":
                case "ItemFilterType":
                case "ItemLabelType":
                case "ItemSortMode":
                case "ItemViewModes":
                case "JournalChangeType":
                case "JournalNotifyOption":
                case "LandingType":
                case "LaserTargettingState":
                case "MechanicalScanType":
                case "meleeMoveDirection":
                case "meleeQueuedAttack":
                case "MessageViewType":
                case "MinigameActionType":
                case "ModuleState":
                case "moveCirclingDirection":
                case "moveExplorationType":
                case "moveLineOfSight":
                case "moveMovementType":
                case "moveSecureFootingFailureReason":
                case "moveSecureFootingFailureType":
                case "NavGenAgentSize":
                case "navNaviPositionType":
                case "operationsMode":
                case "OutcomeMessage":
                case "PackageStatus":
                case "panzerBootupUI":
                case "PaperdollPositionAnimation":
                case "PauseMenuAction":
                case "PaymentStatus":
                case "PerkMenuAttribute":
                case "PersistenceSource":
                case "physicsStateValue":
                case "PlayerVisionModeControllerRefreshPolicyEnum":
                case "PopupPosition":
                case "ProgramEffect":
                case "ProgramType":
                case "ProximityProgressBarOrientation":
                case "ProximityProgressBarState":
                case "PuppetVehicleState":
                case "QuantityPickerActionType":
                case "questJournalAlignmentEventType":
                case "questJournalSizeEventType":
                case "questObjectInspectEventType":
                case "questPhoneCallMode":
                case "questPhoneCallPhase":
                case "questPhoneStatus":
                case "questPhoneTalkingState":
                case "QuickSlotActionType":
                case "QuickSlotItemType":
                case "ReactionZones_Humanoid_Side":
                case "Ref_1_3_3_Colors":
                case "Ref_1_3_3_CustomSize_2":
                case "Ref_2_3_3_Enum32Bit":
                case "RequestType":
                case "RipperdocFilter":
                case "RipperdocModes":
                case "ScannerDataType":
                case "ScannerNetworkState":
                case "ScannerObjectType":
                case "scnDialogLineLanguage":
                case "scnDialogLineType":
                case "scnFastForwardMode":
                case "scnPlayDirection":
                case "scnPlaySpeed":
                case "SecurityEventScopeSettings":
                case "senseEShapeType":
                case "SettingsType":
                case "SignalType":
                case "SignShape":
                case "SignType":
                case "SlotType":
                case "TargetComponentFilterType":
                case "TargetingSet":
                case "telemetryInitalChoiceStage":
                case "telemetryLevelGainReason":
                case "ThrowType":
                case "Tier2WalkType":
                case "TSFMV":
                case "TweakWeaponPose":
                case "UIGameContext":
                case "UIInGameNotificationType":
                case "UIMenuNotificationType":
                case "UIObjectiveEntryType":
                case "vehicleAudioEventAction":
                case "vehicleCameraType":
                case "VehicleDoorInteractionState":
                case "VehicleDoorState":
                case "vehicleELightMode":
                case "vehicleELightType":
                case "vehicleEState":
                case "vehicleExitDirection":
                case "vehicleGarageState":
                case "vehicleQuestUIEnable":
                case "vehicleQuestWindowDestruction":
                case "vehicleRaceUI":
                case "vehicleSummonState":
                case "VendorConfirmationPopupType":
                case "VisionModePatternType":
                case "VisualState":
                case "WeaponPartType":
                case "WorkspotConditionOperators":
                case "WorkspotSlidingBehaviour":
                case "WorkspotWeaponConditionEnum":
                case "workWorkspotDebugMode":
                case "worldgeometryaverageNormalDetectionHelperQueryStatus":
                case "worldgeometryDescriptionQueryFlags":
                case "worldgeometryDescriptionQueryStatus":
                case "worldgeometryProbingStatus":
                case "WorldMapTooltipType":
                case "worldNavigationRequestStatus":
                case "worldOffMeshConnectionType":
                case "worldRainIntensity":
                case "worldTrafficLightColor":
                    var sId = reader.ReadUInt16();
                    var str = stringList[sId];
                    return str;

                case "CC_Debug":
                    var pos2 = reader.BaseStream.Position;
                    var bytes = reader.ReadBytes(128);
                    var hex = BitConverter.ToString(bytes).Replace("-", " ");
                    reader.BaseStream.Position = pos2;
                    try
                    {
                        var t1 = ReadFields(reader, stringList);
                    }
                    catch (Exception e)
                    {
                        throw;
                    }

                    return null;

                // TODO: check if thats always the case for those
                // unknown, 2 bytes
                case "CName":
                case "gameStatIDType":
                case "gameEHotkey":
                case "gameStatPoolsSystemSave":
                case "gameStatPoolDataValueChangeMode":
                case "gameStatPoolDataStatPoolModificationStatus":
                    var sId2 = reader.ReadUInt16();
                    return stringList[sId2];

                case "NodeRef":
                    var size = reader.ReadUInt16();
                    var buffer = reader.ReadBytes(size);
                    return Encoding.ASCII.GetString(buffer);

                default:
                    return ReadFields(reader, stringList);
            }
        }

        public byte[] Write(NodeEntry node, List<INodeParser> parsers)
        {
            byte[] result;
            var data = (GenericUnknownStruct)node.Value;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII))
                {
                    writer.Write(new byte[4]);
                    writer.Write(data.Unknown1);
                    writer.Write(data.Unknown2);
                    writer.Write(data.Unknown3);
                    writer.Write(new byte[12]);

                    if (data.Unknown2 == 16)
                    {
                        writer.Write(data.CNameHashes1.Length);
                        foreach (var hash in data.CNameHashes1)
                        {
                            writer.Write(hash);
                        }
                    }

                    var pos = writer.BaseStream.Position;

                    var stringList = GenerateStringList(data);

                    var offset = (short)(stringList.Count * 4);
                    foreach (var str in stringList)
                    {
                        writer.Write(offset);
                        writer.Write(new byte[] { 0 });
                        writer.Write((byte)(str.Length + 1));
                        offset += (short)(str.Length + 1);
                    }

                    var stringTableOffset = writer.BaseStream.Position - pos;

                    foreach (var str in stringList)
                    {
                        var bytes = Encoding.ASCII.GetBytes(str);
                        writer.Write(bytes);
                        writer.Write(new byte[1]);
                    }

                    var indexTableOffset = writer.BaseStream.Position - pos;

                    foreach (var classEntry in data.ClassList)
                    {
                        var strId = stringList.IndexOf(classEntry.Name);
                        writer.Write(strId);
                        writer.Write(new byte[4]);
                    }

                    var dataTableOffset = writer.BaseStream.Position - pos;

                    for (int i = 0; i < data.ClassList.Length; i++)
                    {
                        var classEntry = data.ClassList[i];

                        var buffer = GenerateDataFromFields(classEntry.Fields, stringList);

                        var classOffset = writer.BaseStream.Position - pos;
                        writer.Write(buffer);
                        var currentPos = writer.BaseStream.Position;

                        writer.BaseStream.Position = pos + indexTableOffset + (i * 8) + 4;
                        writer.Write((uint)classOffset);
                        writer.BaseStream.Position = currentPos;
                    }

                    writer.BaseStream.Position = 0;
                    writer.Write((int)writer.BaseStream.Length - 4);
                    writer.BaseStream.Position += 12;
                    writer.Write((int)stringTableOffset);
                    writer.Write((int)indexTableOffset);
                    writer.Write((int)dataTableOffset);
                }

                result = stream.ToArray();
            }

            if (DEBUG_WRITING)
            {
                File.WriteAllBytes($"C:\\Dev\\T1\\{node.Name}_new.bin", result);
            }

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII))
                {
                    writer.Write(node.Id);
                    writer.Write(result);
                    if (data.CNameHashes2.Length > 0)
                    {
                        writer.Write(data.CNameHashes2.Length);
                        foreach (var hash in data.CNameHashes2)
                        {
                            writer.Write(hash);
                        }
                    }
                }
                result = stream.ToArray();
                node.Size = result.Length;
                node.TrueSize = result.Length;
            }

            return result;
        }

        private List<string> GenerateStringList(GenericUnknownStruct data)
        {
            var result = new List<string>();

            foreach (var classEntry in data.ClassList)
            {
                if (!result.Contains(classEntry.Name))
                {
                    result.Add(classEntry.Name);
                }

                GenerateStringListFromFields(classEntry.Fields, ref result);
            }

            return result;
        }

        private void GenerateStringListFromFields(GenericUnknownStruct.BaseGenericField[] fields, ref List<string> strings)
        {
            foreach (dynamic field in fields)
            {
                if (!strings.Contains(field.Name))
                {
                    strings.Add(field.Name);
                }

                if (!strings.Contains(field.Type))
                {
                    strings.Add(field.Type);
                }

                if (field.Type == "NodeRef")
                {
                    continue;
                }
                else if (field.Value is IList)
                {
                    if (field.Value is GenericUnknownStruct.BaseGenericField[] subFields1)
                    {
                        GenerateStringListFromFields(subFields1, ref strings);
                    }
                    else
                    {
                        var subList = (IList)field.Value;
                        foreach (var t1 in subList)
                        {
                            if (t1 is GenericUnknownStruct.BaseGenericField[] subFields2)
                            {
                                GenerateStringListFromFields(subFields2, ref strings);
                            }
                            else if (t1 is String)
                            {
                                var strVal = (string)t1;
                                if (!strings.Contains(strVal))
                                {
                                    strings.Add(strVal);
                                }
                            }
                        }
                    }
                }
                else if (field.Value is String)
                {
                    var strVal = (string)field.Value;
                    if (!strings.Contains(strVal))
                    {
                        strings.Add(strVal);
                    }
                }
            }
        }

        private byte[] GenerateDataFromFields(GenericUnknownStruct.BaseGenericField[] fields, List<string> stringList)
        {
            byte[] result;

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII))
                {
                    writer.Write((ushort)fields.Length);
                    foreach (var field in fields)
                    {
                        writer.Write((ushort)stringList.IndexOf(field.Name));
                        writer.Write((ushort)stringList.IndexOf(field.Type));
                        writer.Write(new byte[4]); // offset
                    }

                    for (int i = 0; i < fields.Length; i++)
                    {
                        var pos = writer.BaseStream.Position;
                        writer.BaseStream.Position = 6 + (i * 8);
                        writer.Write((uint)pos);
                        writer.BaseStream.Position = pos;

                        dynamic field = fields[i];
                        if (field.Type == "NodeRef")
                        {
                            var valStr = (string)field.Value;
                            var valBytes = Encoding.ASCII.GetBytes(valStr);

                            writer.Write((ushort)valStr.Length);
                            writer.Write(valBytes);
                        }
                        else if (field.Value is IList)
                        {
                            if (field.Value is GenericUnknownStruct.BaseGenericField[] subFields1)
                            {
                                var buffer = GenerateDataFromFields(subFields1, stringList);
                                writer.Write(buffer);
                            }
                            else
                            {
                                var subList = (IList)field.Value;
                                writer.Write(subList.Count);
                                foreach (var t1 in subList)
                                {
                                    if (t1 is GenericUnknownStruct.BaseGenericField[] subFields2)
                                    {
                                        var buffer = GenerateDataFromFields(subFields2, stringList);
                                        writer.Write(buffer);
                                    }
                                    else if (t1 is String)
                                    {
                                        writer.Write((ushort)stringList.IndexOf((string)t1));
                                    }
                                    else
                                    {
                                        WriteValue(writer, t1);
                                    }
                                }
                            }
                        }
                        else if (field.Value is String)
                        {
                            writer.Write((ushort)stringList.IndexOf((string)field.Value));
                        }
                        else
                        {
                            WriteValue(writer, field.Value);
                        }
                    }
                }

                result = stream.ToArray();
            }

            return result;
        }

        private void WriteValue(BinaryWriter writer, object value)
        {
            var type = value.GetType();
            var typeStr = type.FullName;

            switch (typeStr)
            {
                case "System.Int16":
                    writer.Write((short)value);
                    break;

                case "System.UInt16":
                    writer.Write((ushort)value);
                    break;

                case "System.Int32":
                    writer.Write((int)value);
                    break;

                case "System.UInt32":
                    writer.Write((uint)value);
                    break;

                case "System.Int64":
                    writer.Write((long)value);
                    break;

                case "System.UInt64":
                    writer.Write((ulong)value);
                    break;

                case "System.Boolean":
                    writer.Write((bool)value);
                    break;

                case "System.Single":
                    writer.Write((float)value);
                    break;

                default:
                    throw new Exception();
            }
        }
        public class StatSystemEntry
        {
            public ulong EntityHash { get; set; }
            public DumpedEnums.gamedataStatType InactiveStats;
            public uint[] StatModifiers { get; set; }
            public ulong RecordID { get; set; }
            public uint Seed { get; set; }
            public object[] Modifiers { get; set; }
        }
        public static class StatModifierDataConverter
        {
            public static List<StatSystemEntry> Convert(GenericUnknownStruct source, GameStatsStateMapStructure mapping)
            {
                var sourceList = source.ClassList.ToList();
                List<object> modifierList = new List<object>();
                sourceList.RemoveAt(0);
                List<string> names= new List<string>();
                foreach(var classEntry in sourceList)
                {
                    if (classEntry.Name== "gameConstantStatModifierData")
                    {
                        var constantData = new GameConstantStatModifierData();
                        var statTypeString = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "statType"))?.Value;
                        constantData.StatType = (DumpedEnums.gamedataStatType)Enum.Parse(typeof(DumpedEnums.gamedataStatType), statTypeString);
                        var modifierTypeString = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "modifierType"))?.Value;
                        constantData.ModifierType = (DumpedEnums.gameStatModifierType)Enum.Parse(typeof(DumpedEnums.gameStatModifierType), modifierTypeString);
                        constantData.Value = ((GenericUnknownStruct.GenericField<float>)classEntry.Fields.FirstOrDefault(f => f.Name == "value"))?.Value;
                        modifierList.Add(constantData);

                    }
                    else if(classEntry.Name == "gameCurveStatModifierData")
                    {
                        var curveData = new GameCurveStatModifierData();
                        var statTypeString = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "statType"))?.Value;
                        curveData.StatType = (DumpedEnums.gamedataStatType)Enum.Parse(typeof(DumpedEnums.gamedataStatType), statTypeString);
                        var modifierTypeString = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "modifierType"))?.Value;
                        curveData.ModifierType = (DumpedEnums.gameStatModifierType)Enum.Parse(typeof(DumpedEnums.gameStatModifierType), modifierTypeString);
                       
                        curveData.CurveName = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "curveName"))?.Value;
                        curveData.ColumnName = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "columnName"))?.Value;

                        var curveStatString = ((GenericUnknownStruct.GenericField<string>)classEntry.Fields.FirstOrDefault(f => f.Name == "curveStat"))?.Value;
                        curveData.CurveStat = (DumpedEnums.gamedataStatType)Enum.Parse(typeof(DumpedEnums.gamedataStatType), curveStatString);
                        modifierList.Add(curveData);

                    }
                }
                List<StatSystemEntry> result = new List<StatSystemEntry>();
                foreach(var keyValuePair in mapping.MappingStructure)
                {
                    var entry = new StatSystemEntry();
                    entry.EntityHash = keyValuePair.Key.EntityHash;
                    entry.InactiveStats = keyValuePair.Value.InactiveStats;
                    if (keyValuePair.Value.StatModifiers != null)
                    {
                        List<object> modifiers=new List<object>();
                        foreach(var index in keyValuePair.Value.StatModifiers)
                        {
                            modifiers.Add(modifierList[(int)index-1]);
                        }
                        entry.Modifiers = modifiers.ToArray();
                    }
                    entry.RecordID = keyValuePair.Value.RecordID;
                    entry.Seed = keyValuePair.Value.Seed;
                    entry.StatModifiers = keyValuePair.Value.StatModifiers;
                    result.Add(entry);
                }
                return result;
            }
        }
        public class GameCurveStatModifierData
        {
            public DumpedEnums.gamedataStatType StatType { get; set; }
            public DumpedEnums.gameStatModifierType ModifierType { get; set; }
            public string CurveName { get; set;}
            public string ColumnName { get; set; }
            public DumpedEnums.gamedataStatType CurveStat { get; set;}

        }
        public class GameConstantStatModifierData
        {
            public DumpedEnums.gamedataStatType StatType {get; set;}
            public DumpedEnums.gameStatModifierType ModifierType { get; set; }
            public float? Value { get; set;}
        }

        public class GameStatsStateMapStructure
        {
            public Dictionary<GameStatsObjectID, GameSavedStatsData> MappingStructure;
            public GameStatsStateMapStructure(GenericUnknownStruct source)
            {

                MappingStructure = new Dictionary<GameStatsObjectID, GameSavedStatsData>();
               var mappingStructure = source.ClassList.FirstOrDefault(c => c.Name == "gameStatsStateMapStructure");
                var keyList = ((GenericUnknownStruct.GenericField<GenericUnknownStruct.BaseGenericField[][]>)mappingStructure.Fields[0]).Value;
                var valueList = ((GenericUnknownStruct.GenericField<GenericUnknownStruct.BaseGenericField[][]>)mappingStructure.Fields[1]).Value;
                int index = 0;
                foreach (var key in keyList)
                {
                    var keyObject = new GameStatsObjectID();
                    keyObject.EntityHash = ((GenericUnknownStruct.GenericField<ulong>)key[0]).Value;
                    var enumString = ((GenericUnknownStruct.GenericField<string>)key[1]).Value;
                    keyObject.IdType = (DumpedEnums.gameStatIDType)Enum.Parse(typeof(DumpedEnums.gameStatIDType),enumString);


                    var valueObject = new GameSavedStatsData();
                    var valueSource = valueList[index];
                    foreach(var value in valueSource)
                    {
                        if(value is GenericUnknownStruct.GenericField<ulong>)
                        {
                            if(((GenericUnknownStruct.GenericField<ulong>)value).Name== "recordID")
                            {
                                valueObject.RecordID = ((GenericUnknownStruct.GenericField<ulong>)value).Value;
                            }
                            else
                            {
                                Debugger.Break();
                            }
                        }
                        else if(value is GenericUnknownStruct.GenericField<uint>)
                        {
                            if (((GenericUnknownStruct.GenericField<uint>)value).Name == "seed")
                            {
                                valueObject.Seed = ((GenericUnknownStruct.GenericField<uint>)value).Value;
                            }
                            else
                            {
                                Debugger.Break();
                            }
                        }else if (value is GenericUnknownStruct.GenericField<uint[]>)
                        {
                            if(((GenericUnknownStruct.GenericField<uint[]>)value).Name == "statModifiers")
                            {
                                valueObject.StatModifiers = ((GenericUnknownStruct.GenericField<uint[]>)value).Value;
                            }
                            else
                            {
                                Debugger.Break();
                            }
                        }
                        else if(value is GenericUnknownStruct.GenericField<string[]>)
                        {
                            if(((GenericUnknownStruct.GenericField<string[]>)value).Name == "inactiveStats")
                            {
                                var inactiveStatsEnumString = ((GenericUnknownStruct.GenericField<string[]>)value).Value[0];
                                valueObject.InactiveStats = (DumpedEnums.gamedataStatType)Enum.Parse(typeof(DumpedEnums.gamedataStatType), inactiveStatsEnumString);
                                if (((GenericUnknownStruct.GenericField<string[]>)value).Value.Length > 1)
                                {
                                    Debugger.Break();
                                }
                            }
                            else
                            {
                                Debugger.Break();
                            }
                        }
                    }
                    MappingStructure[keyObject] = valueObject;
                    index++;
                }
            }
        }
        [JsonObject]
        public class GameStatsObjectID
        {
            public ulong EntityHash { get; set;}
            public DumpedEnums.gameStatIDType IdType { get; set; }
        }
        [JsonObject]
        public class GameSavedStatsData
        {
            public DumpedEnums.gamedataStatType InactiveStats;
            public uint[] StatModifiers { get; set; }
            public ulong RecordID { get; set; }
            public uint Seed { get; set; }
            
        }
    }
}