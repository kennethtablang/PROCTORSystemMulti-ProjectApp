namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>Set, change or remove the teacher PIN (the toolbar's key button and Settings).</summary>
public static class TeacherPinFlow
{
    public static async Task ManageAsync(Page page)
    {
        if (TeacherPin.IsSet)
        {
            var choice = await page.DisplayActionSheetAsync("Teacher PIN", "Cancel", null, "Change PIN", "Remove PIN");
            if (choice is null or "Cancel") return;
            var current = await page.DisplayPromptAsync("Teacher PIN", "Current PIN:", "OK", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
            if (current is null) return;
            if (!await TeacherPin.VerifyAsync(current)) { await page.DisplayAlertAsync("Teacher PIN", "That PIN isn't right.", "OK"); return; }
            if (choice == "Remove PIN") { TeacherPin.Clear(); await page.DisplayAlertAsync("Teacher PIN", "PIN removed.", "OK"); return; }
        }
        var pin = await page.DisplayPromptAsync("Teacher PIN", $"New PIN ({TeacherPin.MinLength}–{TeacherPin.MaxLength} digits). It's asked when the app opens and when you lock it.", "Next", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
        if (pin is null) return;
        if (!TeacherPin.IsValidFormat(pin)) { await page.DisplayAlertAsync("Teacher PIN", $"Use {TeacherPin.MinLength}–{TeacherPin.MaxLength} digits.", "OK"); return; }
        var again = await page.DisplayPromptAsync("Teacher PIN", "Type it again:", "Save", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
        if (again != pin) { await page.DisplayAlertAsync("Teacher PIN", "The PINs didn't match.", "OK"); return; }
        TeacherPin.Set(pin);
        await page.DisplayAlertAsync("Teacher PIN", "PIN saved. Use \"Lock this app now\" when you step away.", "OK");
    }

    public static async Task LockNowAsync(Page page)
    {
        if (!TeacherPin.IsSet)
        {
            await page.DisplayAlertAsync("Lock this app", "Set a teacher PIN first, then this locks the app until the PIN is entered.", "OK");
            return;
        }
        await page.Navigation.PushModalAsync(new LockPage());
    }
}
