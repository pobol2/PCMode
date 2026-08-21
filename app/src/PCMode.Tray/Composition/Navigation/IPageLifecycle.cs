namespace PCMode.Tray.Navigation;

public interface IPageLifecycle
{
    void OnActivated();
    void OnDeactivated();
}