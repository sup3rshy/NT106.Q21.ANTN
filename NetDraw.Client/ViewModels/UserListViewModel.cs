using System.Collections.ObjectModel;
using NetDraw.Client.Infrastructure;
using NetDraw.Shared.Models;

namespace NetDraw.Client.ViewModels;

public record UserJoinedEvent(UserInfo User);
public record UserLeftEvent(string UserId);
public record UserListUpdatedEvent(List<UserInfo> Users);

public class UserListViewModel : ViewModelBase
{
    public ObservableCollection<UserInfo> Users { get; } = new();

    public UserListViewModel(EventAggregator events)
    {
        events.Subscribe<UserJoinedEvent>(e =>
        {
            if (Users.All(u => u.UserId != e.User.UserId))
                Users.Add(e.User);
        });

        events.Subscribe<UserLeftEvent>(e =>
        {
            var user = Users.FirstOrDefault(u => u.UserId == e.UserId);
            if (user != null) Users.Remove(user);
        });

        events.Subscribe<UserListUpdatedEvent>(e =>
        {
            Users.Clear();
            var seen = new HashSet<string>();
            foreach (var u in e.Users)
                if (seen.Add(u.UserId))
                    Users.Add(u);
        });
    }
}
