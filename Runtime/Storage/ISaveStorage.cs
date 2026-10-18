using System;
using System.Collections.Generic;

namespace SaveStore.Storage
{
    /// <summary>
    /// 세이브가 놓이는 매체의 최소 계약 — 이름 붙은 바이트 덩어리(blob)를 읽고, 내구성 있게 쓰고, 이름을 바꾸고, 지우고, 나열한다.
    /// 세대·manifest·체크섬·롤백 같은 안전 규칙은 <see cref="SaveRepository"/> 가 이 위에서 공통으로 처리하므로,
    /// 새 플랫폼(콘솔 세이브 API, WebGL, 클라우드 캐시 등)은 이 다섯 동작만 구현하면 된다.
    ///
    /// 구현 계약:
    /// <list type="bullet">
    /// <item><see cref="Write"/> 는 반환 시점에 내용이 매체에 확정돼 있어야 한다(파일이면 fsync). 커밋 순서 보장이 이것에 기댄다.</item>
    /// <item><see cref="Rename"/> 은 원자적이어야 한다 — 중간 상태(둘 다 없음/반쯤 복사)가 보이면 안 된다. 대상이 있으면 덮어쓰지 않고 예외를 던진다.</item>
    /// <item>이름은 SaveRepository 가 만든다: 소문자·숫자·<c>_ - .</c> 만 쓴다. 매체가 경로를 쓰더라도 하위 폴더는 없다.</item>
    /// <item>스레드: SaveRepository 가 한 번에 한 스레드에서만 부른다(쓰기 스레드 또는 쓰기가 없을 때 메인). 동시 호출 안전성은 필요 없다.</item>
    /// </list>
    /// </summary>
    public interface ISaveStorage
    {
        /// <summary>
        /// 사람이 읽을 위치 설명(폴더 경로 등). 디버그 표시용.
        /// </summary>
        string Location { get; }

        /// <summary>
        /// 매체를 쓸 준비를 한다(폴더 생성, 마운트 등). 로드 전에 한 번 불린다.
        /// </summary>
        void Open();

        bool TryRead(string name, out byte[] data);

        void Write(string name, ReadOnlySpan<byte> data);

        void Rename(string sourceName, string destinationName);

        /// <summary>
        /// 없으면 아무것도 하지 않는다.
        /// </summary>
        void Delete(string name);

        IReadOnlyList<string> List();
    }
}
