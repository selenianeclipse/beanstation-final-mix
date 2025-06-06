// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2024 Scruq445 <storchdamien@gmail.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Fishbait <Fishbait@git.ml>
// SPDX-FileCopyrightText: 2025 Misandry <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2025 fishbait <gnesse@gmail.com>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.Audio;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Destructible;
using Content.Shared.FixedPoint;
using Content.Shared.Damage;

namespace Content.Goobstation.Shared.Vehicles;

public abstract partial class SharedVehicleSystem : EntitySystem
{
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambientSound = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItem = default!;

    public static readonly EntProtoId HornActionId = "ActionHorn";
    public static readonly EntProtoId SirenActionId = "ActionSiren";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VehicleComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<VehicleComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<VehicleComponent, StrapAttemptEvent>(OnStrapAttempt);
        SubscribeLocalEvent<VehicleComponent, StrappedEvent>(OnStrapped);
        SubscribeLocalEvent<VehicleComponent, UnstrappedEvent>(OnUnstrapped);
        SubscribeLocalEvent<VehicleComponent, VirtualItemDeletedEvent>(OnDropped);

        SubscribeLocalEvent<VehicleComponent, EntInsertedIntoContainerMessage>(OnInsert);
        SubscribeLocalEvent<VehicleComponent, EntRemovedFromContainerMessage>(OnEject);

        SubscribeLocalEvent<VehicleComponent, HornActionEvent>(OnHorn);
        SubscribeLocalEvent<VehicleComponent, SirenActionEvent>(OnSiren);
        SubscribeLocalEvent<VehicleComponent, ItemSlotEjectAttemptEvent>(OnItemSlotEject);
        SubscribeLocalEvent<VehicleComponent, BreakageEventArgs>(OnBreak);
        SubscribeLocalEvent<VehicleComponent, DamageChangedEvent>(OnRepair);
    }

    private void OnInit(EntityUid uid, VehicleComponent component, ComponentInit args)
    {
        _appearance.SetData(uid, VehicleState.Animated, component.EngineRunning);
        _appearance.SetData(uid, VehicleState.DrawOver, false);
    }

    private void OnRemove(EntityUid uid, VehicleComponent component, ComponentRemove args)
    {
        if (component.Driver == null)
            return;

        _buckle.TryUnbuckle(component.Driver.Value, component.Driver.Value);
        Dismount(component.Driver.Value, uid);
        _appearance.SetData(uid, VehicleState.DrawOver, false);
    }

    private void OnInsert(EntityUid uid, VehicleComponent component, ref EntInsertedIntoContainerMessage args)
    {
        if (HasComp<InstantActionComponent>(args.Entity))
            return;

        if (args.Container.ID == component.KeySlot && !component.IsBroken)
        {
            component.EngineRunning = true;
            _appearance.SetData(uid, VehicleState.Animated, true);

            _ambientSound.SetAmbience(uid, true);


            if (component.Driver == null)
                return;

            Mount(component.Driver.Value, uid);
        }
    }

    private void OnEject(EntityUid uid, VehicleComponent component, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == component.KeySlot)
        {
            component.EngineRunning = false;
            _appearance.SetData(uid, VehicleState.Animated, false);

            _ambientSound.SetAmbience(uid, false);

            if (component.Driver == null)
                return;

            Dismount(component.Driver.Value, uid);
        }
    }

    private void OnHorn(EntityUid uid, VehicleComponent component, InstantActionEvent args)
    {
        if (args.Handled == true)
            return;

        if (component.Driver != args.Performer)
            return;

        if (component.HornSound == null)
            return;

        _audio.PlayPvs(component.HornSound, uid);
        args.Handled = true;
    }

    private void OnSiren(EntityUid uid, VehicleComponent component, InstantActionEvent args)
    {
        if (args.Handled == true)
            return;

        if (component.Driver != args.Performer)
            return;

        if (component.SirenSound == null)
            return;

        if (component.SirenEnabled)
        {
            component.SirenStream = _audio.Stop(component.SirenStream);
        }
        else
        {
            component.SirenStream = _audio.PlayPvs(component.SirenSound, uid)?.Entity;
        }

        component.SirenEnabled = !component.SirenEnabled;
        args.Handled = true;
    }

    private void OnStrapAttempt(Entity<VehicleComponent> ent, ref StrapAttemptEvent args)
    {
        var driver = args.Buckle.Owner; // i dont want to re write this shit 100 fucking times

        if (ent.Comp.Driver != null)
        {
            args.Cancelled = true;
            return;
        }

        if (ent.Comp.RequiredHands != 0)
        {
            for (int hands = 0; hands < ent.Comp.RequiredHands; hands++)
            {
                if (!_virtualItem.TrySpawnVirtualItemInHand(ent.Owner, driver, false))
                {
                    args.Cancelled = true;
                    _virtualItem.DeleteInHandsMatching(driver, ent.Owner);
                    return;
                }
            }
        }

        AddHorns(driver, ent);
    }

    private void OnStrapped(Entity<VehicleComponent> ent, ref StrappedEvent args)
    {
        var driver = args.Buckle.Owner;

        if (!TryComp(driver, out MobMoverComponent? mover))
            return;

        if (ent.Comp.Driver != null)
            return;

        ent.Comp.Driver = driver;
        _appearance.SetData(ent.Owner, VehicleState.DrawOver, true);

        if (!ent.Comp.EngineRunning)
            return;

        Mount(driver, ent.Owner);
    }

    private void OnUnstrapped(Entity<VehicleComponent> ent, ref UnstrappedEvent args)
    {
        if (ent.Comp.Driver != args.Buckle.Owner)
            return;

        Dismount(args.Buckle.Owner, ent);
        _appearance.SetData(ent.Owner, VehicleState.DrawOver, false);
    }

    private void OnDropped(EntityUid uid, VehicleComponent comp, VirtualItemDeletedEvent args)
    {
        if (comp.Driver != args.User)
            return;

        _buckle.TryUnbuckle(args.User, args.User);

        Dismount(args.User, uid);
        _appearance.SetData(uid, VehicleState.DrawOver, false);
    }

    private void AddHorns(EntityUid driver, EntityUid vehicle)
    {
        if (!TryComp<VehicleComponent>(vehicle, out var vehicleComp))
            return;

        if (vehicleComp.HornSound != null)
            _actions.AddAction(driver, ref vehicleComp.HornAction, HornActionId, vehicle);

        if (vehicleComp.SirenSound != null)
            _actions.AddAction(driver, ref vehicleComp.SirenAction, SirenActionId, vehicle);
    }

    private void Mount(EntityUid driver, EntityUid vehicle)
    {
        if (TryComp<AccessComponent>(vehicle, out var accessComp))
        {
            var accessSources = _access.FindPotentialAccessItems(driver);
            var access = _access.FindAccessTags(driver, accessSources);

            foreach (var tag in access)
            {
                accessComp.Tags.Add(tag);
            }
        }

        _mover.SetRelay(driver, vehicle);
    }

    private void Dismount(EntityUid driver, EntityUid vehicle)
    {
        if (!TryComp<VehicleComponent>(vehicle, out var vehicleComp))
            return;

        if (vehicleComp.Driver != driver)
            return;

        RemComp<RelayInputMoverComponent>(driver);

        vehicleComp.Driver = null;

        if (vehicleComp.HornAction != null)
            _actions.RemoveAction(driver, vehicleComp.HornAction);

        if (vehicleComp.SirenAction != null)
            _actions.RemoveAction(driver, vehicleComp.SirenAction);

        _virtualItem.DeleteInHandsMatching(driver, vehicle);

        if (TryComp<AccessComponent>(vehicle, out var accessComp))
            accessComp.Tags.Clear();
    }

    private void OnItemSlotEject(EntityUid uid, VehicleComponent comp, ref ItemSlotEjectAttemptEvent args)
    {
        if (!comp.PreventEjectOfKey)
            return;
        if (comp.Driver == null)
            return;
        if (args.Slot.ID != comp.KeySlot)
            return;
        if (args.User == comp.Driver)
            return;

        args.Cancelled = true;
    }

    private void OnBreak(EntityUid uid, VehicleComponent component, BreakageEventArgs args)
    {
        component.IsBroken = true;

        //remove dirvers ability to drive if there is a driver
        if (component.Driver != null)
            Dismount(component.Driver.Value, uid);

        //stop animation
        component.EngineRunning = false;
        _appearance.SetData(uid, VehicleState.Animated, false);
        _ambientSound.SetAmbience(uid, false);
    }

    private void OnRepair(EntityUid uid, VehicleComponent component, DamageChangedEvent args)
    {
        if (!component.IsBroken)
            return;
        if (args.Damageable.TotalDamage == FixedPoint2.Zero)
            component.IsBroken = false;
    }

}

public sealed partial class HornActionEvent : InstantActionEvent;

public sealed partial class SirenActionEvent : InstantActionEvent;