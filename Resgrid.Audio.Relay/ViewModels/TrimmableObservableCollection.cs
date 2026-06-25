using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// An <see cref="ObservableCollection{T}"/> that can drop its oldest items in a single pass.
	/// <see cref="TrimToLast"/> removes the overflow with one backing-store <see cref="List{T}.RemoveRange"/>
	/// and raises a single <see cref="NotifyCollectionChangedAction.Reset"/> — avoiding the O(n²)
	/// array shifts and per-item <c>CollectionChanged</c> notifications of repeated <c>RemoveAt(0)</c>
	/// (which freezes the UI when trimming a large ring buffer on a burst).
	/// </summary>
	public sealed class TrimmableObservableCollection<T> : ObservableCollection<T>
	{
		private static readonly PropertyChangedEventArgs CountChanged = new PropertyChangedEventArgs(nameof(Count));
		private static readonly PropertyChangedEventArgs IndexerChanged = new PropertyChangedEventArgs("Item[]");

		/// <summary>
		/// Drops the oldest items so at most <paramref name="max"/> remain (a no-op when already at or
		/// under the cap). Performs one <see cref="List{T}.RemoveRange"/> on the backing store, then
		/// raises a single Reset notification rather than one per removed element.
		/// </summary>
		public void TrimToLast(int max)
		{
			if (max < 0)
				max = 0;

			var overflow = Count - max;
			if (overflow <= 0)
				return;

			// ObservableCollection's backing store is a List<T>; remove the oldest items in one shift.
			((List<T>)Items).RemoveRange(0, overflow);

			OnPropertyChanged(CountChanged);
			OnPropertyChanged(IndexerChanged);
			OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}
	}
}
