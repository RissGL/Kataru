namespace AudioBookPlayer.ViewModels
{
    /// <summary>
    /// 下拉框里的一个选项（键 / 显示名）。
    ///
    /// 为什么不直接用值元组 (string Key, string Label)：
    /// DataTemplate / DisplayMemberPath 走的是反射，而值元组的 Key/Label 只是编译期别名，
    /// 运行时字段叫 Item1/Item2 —— 绑上去每个选项都是空白（踩过）。
    /// </summary>
    public sealed class Choice
    {
        public Choice(string key, string label)
        {
            Key = key;
            Label = label;
        }

        public string Key { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }
}