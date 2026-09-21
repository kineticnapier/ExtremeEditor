using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    // Keyboard behavior in this file intentionally follows scnEditor.RegisterKeybinds
    // from ADOFAI rather than normal WPF editing conventions. In particular:
    //   Backspace = delete selected floor(s)
    //   Delete    = delete the floor after a single selection
    // and the QWE/ASD/etc. keys create floors directly.
    private void AdoFaiPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        bool windows = (modifiers & ModifierKeys.Windows) != 0;
        bool backQuote = Keyboard.IsKeyDown(Key.Oem3);

        // ADOFAI treats BackQuote as a real modifier, not as a normal key.
        if (key == Key.Oem3 && !control && !alt && !windows)
        {
            e.Handled = true;
            return;
        }

        // BackQuote chords are exclusive in the game's keybind manager. They must
        // not accidentally fall through to Space/P/navigation/etc. while ` is held.
        if (backQuote)
        {
            if (!control && !alt && !windows && _level is not null && Viewport.SelectedFloor >= 0)
            {
                EditorSession? chordEditor = EnsureEditorSession();
                int chordPrimary = Viewport.SelectedFloor;
                if (chordEditor is not null)
                {
                    if (key == Key.Tab)
                    {
                        chordEditor.InsertMidspin(chordPrimary);
                        RefreshAfterAdoFaiMutation(chordPrimary + 1);
                    }
                    else if (GetAdoFaiDirectionForKey(key, backQuote: true) is double chordDirection)
                    {
                        chordEditor.InsertAngle(chordPrimary, chordDirection);
                        RefreshAfterAdoFaiMutation(chordPrimary + 1);
                    }
                }
            }

            e.Handled = true;
            return;
        }

        // File commands that already exist in ExtremeEditor.
        if (!alt && !windows)
        {
            if (control && shift && key == Key.S)
            {
                ExecuteRouted(EditorCommands.SaveAs);
                e.Handled = true;
                return;
            }
            if (control && !shift && key == Key.S)
            {
                ExecuteRouted(EditorCommands.Save);
                e.Handled = true;
                return;
            }
            if (control && !shift && key == Key.O)
            {
                ExecuteRouted(EditorCommands.Open);
                e.Handled = true;
                return;
            }
        }

        // P / Space = play. Ctrl+P / Ctrl+Space is ADOFAI's "play with speed".
        // ExtremeEditor does not have a separate editor-speed selector yet, so both
        // currently use the same transport while keeping the game's exact key slots.
        if (!alt && !windows && !shift &&
            (key == Key.P || key == Key.Space) &&
            (!control || control))
        {
            TogglePlayback();
            e.Handled = true;
            return;
        }

        if (_level is null)
            return;

        // Escape deselects floors. ADOFAI also toggles its file-actions panel here;
        // ExtremeEditor does not currently have that panel.
        if (!control && !shift && !alt && !windows && key == Key.Escape)
        {
            Viewport.SetSelection([]);
            NativeViewport.SetSelection([], -1);
            RefreshInspector();
            CommandManager.InvalidateRequerySuggested();
            e.Handled = true;
            return;
        }

        int primary = Viewport.SelectedFloor;
        if (primary < 0)
            return;

        // Navigation: exactly mirrors the floor-selection registrations in scnEditor.
        if (!alt && !windows && TryHandleAdoFaiNavigation(key, control, shift))
        {
            e.Handled = true;
            return;
        }

        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        // Clipboard and history. Event-only clipboard combinations (Ctrl+Shift+*) are
        // intentionally left for the event editor instead of accidentally copying floors.
        if (!alt && !windows && control && !shift)
        {
            switch (key)
            {
                case Key.C:
                    editor.CopyFloors(Viewport.SelectedFloors);
                    StatusText.Text = $"Copied {Viewport.SelectedFloors.Count:N0} floor(s)";
                    CommandManager.InvalidateRequerySuggested();
                    e.Handled = true;
                    return;

                case Key.X:
                {
                    int target = Math.Max(0, Viewport.SelectedFloors.DefaultIfEmpty(1).Min() - 1);
                    editor.CutFloors(Viewport.SelectedFloors);
                    RefreshAfterAdoFaiMutation(target);
                    e.Handled = true;
                    return;
                }

                case Key.V:
                    if (editor.HasClipboard)
                    {
                        int inserted = primary + 1;
                        editor.PasteFloors(primary);
                        RefreshAfterAdoFaiMutation(inserted);
                    }
                    e.Handled = true;
                    return;

                case Key.Z:
                    if (editor.CanUndo)
                    {
                        editor.Undo();
                        RefreshAfterAdoFaiMutation(primary);
                    }
                    e.Handled = true;
                    return;

                case Key.Y:
                    if (editor.CanRedo)
                    {
                        editor.Redo();
                        RefreshAfterAdoFaiMutation(primary);
                    }
                    e.Handled = true;
                    return;
            }
        }

        if (!alt && !windows && control && shift && key == Key.Z)
        {
            if (editor.CanRedo)
            {
                editor.Redo();
                RefreshAfterAdoFaiMutation(primary);
            }
            e.Handled = true;
            return;
        }

        // ADOFAI deletion semantics are deliberately asymmetric.
        if (!alt && !windows && TryHandleAdoFaiDelete(editor, key, control, shift))
        {
            e.Handled = true;
            return;
        }

        // Transform shortcuts from scnEditor.RegisterKeybinds.
        if (!alt && !windows && control)
        {
            int[] selection = Viewport.SelectedFloors.ToArray();
            if (!shift && key == Key.L)
            {
                editor.FlipHorizontal(selection);
                RefreshAfterAdoFaiMutation(primary, selection);
                e.Handled = true;
                return;
            }
            if (shift && key == Key.L)
            {
                editor.FlipVertical(selection);
                RefreshAfterAdoFaiMutation(primary, selection);
                e.Handled = true;
                return;
            }
            if (!shift && key == Key.OemComma)
            {
                // ADOFAI float directions increase counter-clockwise.
                editor.Rotate(selection, 90.0);
                RefreshAfterAdoFaiMutation(primary, selection);
                e.Handled = true;
                return;
            }
            if (!shift && key == Key.OemPeriod)
            {
                editor.Rotate(selection, -90.0);
                RefreshAfterAdoFaiMutation(primary, selection);
                e.Handled = true;
                return;
            }
            if (!shift && key == Key.OemQuestion)
            {
                editor.Rotate(selection, 180.0);
                RefreshAfterAdoFaiMutation(primary, selection);
                e.Handled = true;
                return;
            }
        }

        if (control || alt || windows)
            return;

        // Plain Tab = Midspin. `+Tab was handled by the BackQuote chord block above.
        // Shift+Tab is not a Midspin binding in ADOFAI.
        if (!shift && key == Key.Tab)
        {
            int inserted = primary + 1;
            editor.InsertMidspin(primary);
            RefreshAfterAdoFaiMutation(inserted);
            e.Handled = true;
            return;
        }

        // Shift+Space is the 360-degree-floor shortcut. Plain Space was handled as Play above.
        if (shift && key == Key.Space)
        {
            int inserted = primary + 1;
            editor.InsertFullTurn(primary);
            RefreshAfterAdoFaiMutation(inserted);
            e.Handled = true;
            return;
        }

        // Enter opens the arbitrary-angle editor.
        if (!shift && key == Key.Enter)
        {
            double initial = GetAdoFaiPreviousDirection(primary);
            double? angle = InputDialog.AskDouble(
                this,
                "Create arbitrary floor",
                "Absolute ADOFAI angle in degrees:",
                initial);
            if (angle is not null)
            {
                int inserted = primary + 1;
                editor.InsertAngle(primary, angle.Value);
                RefreshAfterAdoFaiMutation(inserted);
            }
            e.Handled = true;
            return;
        }

        // Legacy polygon keys. pathData chars 5/6 are +/-72 relative turns;
        // 7/8 are +/-52, which is what the game itself converts to angleData.
        if (key == Key.F5)
        {
            InsertAdoFaiRelativeFloor(editor, primary, shift ? -72.0 : 72.0);
            e.Handled = true;
            return;
        }
        if (key == Key.F7)
        {
            InsertAdoFaiRelativeFloor(editor, primary, shift ? -52.0 : 52.0);
            e.Handled = true;
            return;
        }

        // Floor creation ring. Shift is explicitly registered as equivalent to no modifier.
        if (GetAdoFaiDirectionForKey(key, backQuote: false) is double direction)
        {
            int inserted = primary + 1;
            editor.InsertAngle(primary, direction);
            RefreshAfterAdoFaiMutation(inserted);
            e.Handled = true;
        }
    }

    private bool TryHandleAdoFaiNavigation(Key key, bool control, bool shift)
    {
        if (_level is null || Viewport.SelectedFloor < 0)
            return false;

        int target;
        bool extend;
        if (key == Key.Home && !control && !shift)
        {
            target = 0;
            extend = false;
        }
        else if (key == Key.End && !control && !shift)
        {
            target = Math.Max(0, _level.FloorCount - 1);
            extend = false;
        }
        else if (key == Key.Left)
        {
            if (control)
                target = 0;
            else
                target = Math.Max(0, Viewport.SelectedFloor - 1);
            extend = shift;
        }
        else if (key == Key.Right)
        {
            if (control)
                target = Math.Max(0, _level.FloorCount - 1);
            else
                target = Math.Min(_level.FloorCount - 1, Viewport.SelectedFloor + 1);
            extend = shift;
        }
        else
        {
            return false;
        }

        Viewport.MoveSelection(target, extend);
        NativeViewport.SetSelection(Viewport.SelectedFloors, Viewport.SelectedFloor);
        return true;
    }

    private bool TryHandleAdoFaiDelete(
        EditorSession editor,
        Key key,
        bool control,
        bool shift)
    {
        if (shift || (key != Key.Back && key != Key.Delete))
            return false;

        int[] selected = Viewport.SelectedFloors.OrderBy(floor => floor).ToArray();
        if (selected.Length == 0)
            return true;

        int first = selected[0];
        if (control)
        {
            if (key == Key.Back)
            {
                // DeletePrecedingFloors repeatedly backspaces floor 1, so the first
                // selected floor itself is also removed.
                if (first <= 0)
                    return true;
                editor.DeleteFloors(Enumerable.Range(1, first));
                RefreshAfterAdoFaiMutation(0);
                return true;
            }

            // DeleteSubsequentFloors preserves the first selected floor.
            int last = _level!.FloorCount - 1;
            if (first < last)
            {
                editor.DeleteFloors(Enumerable.Range(first + 1, last - first));
                RefreshAfterAdoFaiMutation(first);
            }
            return true;
        }

        if (selected.Length == 1)
        {
            if (key == Key.Back)
            {
                if (first == 0)
                    return true;
                editor.DeleteFloors([first]);
                RefreshAfterAdoFaiMutation(first - 1);
                return true;
            }

            int next = first + 1;
            if (next >= _level!.FloorCount)
                return true;
            editor.DeleteFloors([next]);
            RefreshAfterAdoFaiMutation(first);
            return true;
        }

        // Multi-selection deletes the selected floors for both keys; only the
        // resulting selection differs, exactly like DeleteMultiSelection(bool).
        if (first == 0)
            return true;
        editor.DeleteFloors(selected);
        int afterDeleteCount = _level!.FloorCount;
        int target = key == Key.Back
            ? first - 1
            : first < afterDeleteCount ? first : Math.Max(0, first - 1);
        RefreshAfterAdoFaiMutation(target);
        return true;
    }

    private void InsertAdoFaiRelativeFloor(EditorSession editor, int primary, double delta)
    {
        double angle = NormalizeAdoFaiAngle(GetAdoFaiPreviousDirection(primary) + delta);
        int inserted = primary + 1;
        editor.InsertAngle(primary, angle);
        RefreshAfterAdoFaiMutation(inserted);
    }

    private double GetAdoFaiPreviousDirection(int floor)
    {
        if (_level is null || floor <= 0 || _level.Angles.Length == 0)
            return 0.0;

        int index = Math.Clamp(floor - 1, 0, _level.Angles.Length - 1);
        double angle = _level.Angles[index];
        return Math.Abs(angle - 999.0) < 0.000001 ? 0.0 : angle;
    }

    private static double NormalizeAdoFaiAngle(double angle)
    {
        double result = angle % 360.0;
        return result < 0.0 ? result + 360.0 : result;
    }

    private static double? GetAdoFaiDirectionForKey(Key key, bool backQuote)
    {
        // Cardinal/45-degree keys are unchanged by BackQuote.
        double? baseDirection = key switch
        {
            Key.D => 0.0,
            Key.E => 45.0,
            Key.W => 90.0,
            Key.Q => 135.0,
            Key.A => 180.0,
            Key.Z => 225.0,
            Key.S or Key.X => 270.0,
            Key.C => 315.0,
            _ => null
        };
        if (baseDirection is not null)
            return baseDirection;

        return backQuote
            ? key switch
            {
                Key.J => 15.0,
                Key.Y => 75.0,
                Key.T => 105.0,
                Key.H => 165.0,
                Key.N => 195.0,
                Key.V => 255.0,
                Key.B => 285.0,
                Key.M => 345.0,
                _ => null
            }
            : key switch
            {
                Key.J => 30.0,
                Key.Y => 60.0,
                Key.T => 120.0,
                Key.H => 150.0,
                Key.N => 210.0,
                Key.V => 240.0,
                Key.B => 300.0,
                Key.M => 330.0,
                _ => null
            };
    }

    private void RefreshAfterAdoFaiMutation(
        int preferredPrimary,
        IEnumerable<int>? preferredSelection = null)
    {
        RefreshEditorAfterMutation(preferredPrimary, preferredSelection);
        NativeViewport.SetSelection(Viewport.SelectedFloors, Viewport.SelectedFloor);
    }

    private void ExecuteRouted(RoutedCommand command)
    {
        if (command.CanExecute(null, this))
            command.Execute(null, this);
    }
}
