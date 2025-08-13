using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AwsManager.Models
{
    public class TagModel : INotifyPropertyChanged
    {
        private string _key = string.Empty;
        public string Key
        {
            get => _key;
            set => SetField(ref _key, value);
        }

        private string _value = string.Empty;
        public string Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
