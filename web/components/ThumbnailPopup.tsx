import React from 'react';
import type { SpriteStatus } from '../types';
import { formatTimecode } from '../utils/timecode';

interface ThumbnailPopupProps {
  time: number;
  x: number;
  containerWidth: number;
  spriteStatus: SpriteStatus | null;
}

export const ThumbnailPopup: React.FC<ThumbnailPopupProps> = ({
  time,
  x,
  containerWidth,
  spriteStatus,
}) => {
  const thumbWidth = spriteStatus?.thumbWidth ?? 160;
  const thumbHeight = spriteStatus?.thumbHeight ?? 90;
  const popupWidth = thumbWidth + 8;
  const clampedLeft = Math.max(8, Math.min(containerWidth - popupWidth - 8, x - popupWidth / 2));

  let sheetUrl: string | null = null;
  let offsetX = 0;
  let offsetY = 0;

  if (spriteStatus && spriteStatus.sheetUrls.length > 0) {
    const perSheet = spriteStatus.columns * spriteStatus.rows;
    const index = Math.max(0, Math.floor(time / spriteStatus.intervalSeconds));
    const sheetIdx = Math.floor(index / perSheet);
    const cellIdx = index % perSheet;
    const col = cellIdx % spriteStatus.columns;
    const row = Math.floor(cellIdx / spriteStatus.columns);

    if (sheetIdx < spriteStatus.sheetUrls.length) {
      sheetUrl = spriteStatus.sheetUrls[sheetIdx] ?? null;
      offsetX = col * spriteStatus.thumbWidth;
      offsetY = row * spriteStatus.thumbHeight;
    }
  }

  return (
    <div
      className="pointer-events-none absolute bottom-full z-30 mb-2 rounded border border-[#2E3440] bg-[#1A1D23] p-1 shadow-xl"
      style={{ left: `${clampedLeft}px`, width: `${popupWidth}px` }}
    >
      {sheetUrl ? (
        <div
          className="overflow-hidden rounded-sm bg-[#090A0C]"
          style={{
            width: `${thumbWidth}px`,
            height: `${thumbHeight}px`,
            backgroundImage: `url(${sheetUrl})`,
            backgroundPosition: `-${offsetX}px -${offsetY}px`,
            backgroundRepeat: 'no-repeat',
          }}
        />
      ) : (
        <div
          className="flex items-center justify-center rounded-sm bg-[#090A0C] text-[11px] text-[#9AA3B2]"
          style={{ width: `${thumbWidth}px`, height: '42px' }}
        >
          Aperçu en préparation
        </div>
      )}
      <div className="font-mono-tabular mt-1 text-center text-[11px] font-medium text-[#EDF0F5]">
        {formatTimecode(time, true)}
      </div>
    </div>
  );
};
