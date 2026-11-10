/*
 * Copyright (c) 2026 Arsen
 *
 * This file is part of MusicBay
 *
 * MusicBay is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * MusicBay is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with MusicBay. If not, see <http://www.gnu.org/licenses/>.
 */

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Google.Android.Material.ProgressIndicator;

namespace Seeker.Player
{
    /// <summary>"Downloading 2 of 5 · title" with a bar at the top of the Downloads tab while <see cref="StreamDownloads"/> works; hidden otherwise.</summary>
    internal class DownloadProgressView : LinearLayout
    {
        readonly TextView label;
        readonly LinearProgressIndicator bar;

        public DownloadProgressView(Context context) : base(context)
        {
            Orientation = Orientation.Vertical;
            float density = context.Resources.DisplayMetrics.Density;
            SetPadding((int)(16 * density), (int)(10 * density), (int)(16 * density), (int)(6 * density));
            label = new TextView(context) { TextSize = 13 };
            label.SetTextColor(new Color(PlayerFormat.ThemeColor(context, Resource.Attribute.cellTextColorSubdued)));
            label.SetSingleLine(true);
            label.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            AddView(label);
            bar = new LinearProgressIndicator(context) { TrackCornerRadius = (int)(2 * density) };
            bar.SetIndicatorColor(PlayerFormat.ThemeColor(context, Resource.Attribute.mainPurple));
            AddView(bar, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = (int)(6 * density) });
            Visibility = ViewStates.Gone;
        }

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            StreamDownloads.Changed += Refresh;
            Refresh();
        }

        protected override void OnDetachedFromWindow()
        {
            StreamDownloads.Changed -= Refresh;
            base.OnDetachedFromWindow();
        }

        void Refresh()
        {
            if (StreamDownloads.Current != null)
            {
                label.Text = Context.GetString(Resource.String.stream_downloading_now, StreamDownloads.Done + 1, StreamDownloads.Total, StreamDownloads.Current);
                bar.Visibility = ViewStates.Visible;
                bar.Max = StreamDownloads.Total;
                bar.SetProgressCompat(StreamDownloads.Done, true);
                Visibility = ViewStates.Visible;
            }
            else if (StreamDownloads.Message != null)
            {
                label.Text = StreamDownloads.Message;
                bar.Visibility = ViewStates.Gone;
                Visibility = ViewStates.Visible;
            }
            else
            {
                Visibility = ViewStates.Gone;
            }
        }
    }
}
