using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace IWMF.ColumnAutoModel.Core
{
    // 保留原对象引用，筛选／排序不改变建模勾选及源数据顺序。
    public class ReviewList<T> : BindingList<T>
    {
        private PropertyDescriptor property;
        private ListSortDirection direction;
        private readonly Func<T, int> priority;
        public ReviewList(IEnumerable<T> rows, Func<T, int> groupPriority = null) : base((groupPriority == null ? rows : rows.OrderBy(groupPriority)).ToList()) { priority = groupPriority; }
        protected override bool SupportsSortingCore => true;
        protected override bool IsSortedCore => property != null;
        protected override PropertyDescriptor SortPropertyCore => property;
        protected override ListSortDirection SortDirectionCore => direction;
        protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection sortDirection)
        {
            // 可选的固定分组优先级不随列排序方向翻转，组内仍支持普通排序。
            var sorted = priority != null
                ? (sortDirection == ListSortDirection.Ascending ? Items.OrderBy(priority).ThenBy(x => prop.GetValue(x), Comparer<object>.Default) : Items.OrderBy(priority).ThenByDescending(x => prop.GetValue(x), Comparer<object>.Default)).ToList()
                : sortDirection == ListSortDirection.Ascending
                ? Items.OrderBy(x => prop.GetValue(x), Comparer<object>.Default).ToList()
                : Items.OrderByDescending(x => prop.GetValue(x), Comparer<object>.Default).ToList();
            RaiseListChangedEvents = false;
            for (int i = 0; i < sorted.Count; i++) Items[i] = sorted[i];
            property = prop; direction = sortDirection; RaiseListChangedEvents = true; ResetBindings();
        }
        public static List<T> Filter(IEnumerable<T> source, string query, Func<T, string> text, Func<T, bool> category)
        {
            var words = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return source.Where(x => category(x) && words.All(w => (text(x) ?? "").IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
        }
    }
}
